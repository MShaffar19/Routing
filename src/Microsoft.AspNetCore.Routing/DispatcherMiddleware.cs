﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing.Matchers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.AspNetCore.Routing
{
    internal sealed class DispatcherMiddleware
    {
        private readonly MatcherFactory _matcherFactory;
        private readonly ILogger _logger;
        private readonly IOptions<DispatcherOptions> _options;
        private readonly RequestDelegate _next;

        private Task<Matcher> _initializationTask;

        public DispatcherMiddleware(
            MatcherFactory matcherFactory,
            IOptions<DispatcherOptions> options,
            ILogger<DispatcherMiddleware> logger,
            RequestDelegate next)
        {
            if (matcherFactory == null)
            {
                throw new ArgumentNullException(nameof(matcherFactory));
            }

            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (logger == null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            if (next == null)
            {
                throw new ArgumentNullException(nameof(next));
            }

            _matcherFactory = matcherFactory;
            _options = options;
            _logger = logger;
            _next = next;
        }

        public async Task Invoke(HttpContext httpContext)
        {
            var feature = new EndpointFeature();
            httpContext.Features.Set<IEndpointFeature>(feature);

            // There's an inherent race condition between waiting for init and accessing the matcher
            // this is OK because once `_matcher` is initialized, it will not be set to null again.
            var matcher = await InitializeAsync();

            await matcher.MatchAsync(httpContext, feature);
            if (feature.Endpoint != null)
            {
                Log.MatchSuccess(_logger, feature);
            }
            else
            {
                Log.MatchFailure(_logger);
            }

            await _next(httpContext);
        }

        // Initialization is async to avoid blocking threads while reflection and things
        // of that nature take place.
        //
        // We've seen cases where startup is very slow if we  allow multiple threads to race 
        // while initializing the set of endpoints/routes. Doing CPU intensive work is a 
        // blocking operation if you have a low core count and enough work to do.
        private Task<Matcher> InitializeAsync()
        {
            if (_initializationTask != null)
            {
                return _initializationTask;
            }

            var initializationTask = new TaskCompletionSource<Matcher>();
            if (Interlocked.CompareExchange<Task<Matcher>>(
                ref _initializationTask,
                initializationTask.Task,
                null) == null)
            {
                // This thread won the race, do the initialization.
                var dataSource = new CompositeEndpointDataSource(_options.Value.DataSources);
                var matcher = _matcherFactory.CreateMatcher(dataSource);
                initializationTask.SetResult(matcher);
            }

            return _initializationTask;
        }

        private static class Log
        {
            private static readonly Action<ILogger, string, Exception> _matchSuccess = LoggerMessage.Define<string>(
                LogLevel.Debug,
                new EventId(1, "MatchSuccess"),
                "Request matched endpoint '{EndpointName}'.");

            private static readonly Action<ILogger, Exception> _matchFailure = LoggerMessage.Define(
                LogLevel.Debug,
                new EventId(2, "MatchFailure"),
                "Request did not match any endpoints.");

            public static void MatchSuccess(ILogger logger, EndpointFeature feature)
            {
                _matchSuccess(logger, feature.Endpoint.DisplayName, null);
            }

            public static void MatchFailure(ILogger logger)
            {
                _matchFailure(logger, null);
            }
        }
    }
}