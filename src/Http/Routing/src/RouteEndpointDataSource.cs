// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace Microsoft.AspNetCore.Routing;

internal sealed class RouteEndpointDataSource : EndpointDataSource
{
    private readonly List<RouteEntry> _routeEntries = new();
    private readonly IServiceProvider _applicationServices;
    private readonly bool _throwOnBadRequest;

    public RouteEndpointDataSource(IServiceProvider applicationServices, bool throwOnBadRequest)
    {
        _applicationServices = applicationServices;
        _throwOnBadRequest = throwOnBadRequest;
    }

    public RouteHandlerBuilder AddRequestDelegate(
        RoutePattern pattern,
        RequestDelegate requestDelegate,
        IEnumerable<string>? httpMethods)
    {

        var conventions = new ThrowOnAddAfterEndpointBuiltConventionCollection();

        _routeEntries.Add(new()
        {
            RoutePattern = pattern,
            RouteHandler = requestDelegate,
            HttpMethods = httpMethods,
            RouteAttributes = RouteAttributes.None,
            Conventions = conventions,
        });

        return new RouteHandlerBuilder(conventions);
    }

    public RouteHandlerBuilder AddRouteHandler(
        RoutePattern pattern,
        Delegate routeHandler,
        IEnumerable<string>? httpMethods,
        bool isFallback)
    {
        var conventions = new ThrowOnAddAfterEndpointBuiltConventionCollection();

        var routeAttributes = RouteAttributes.RouteHandler;
        if (isFallback)
        {
            routeAttributes |= RouteAttributes.Fallback;
        }

        _routeEntries.Add(new()
        {
            RoutePattern = pattern,
            RouteHandler = routeHandler,
            HttpMethods = httpMethods,
            RouteAttributes = routeAttributes,
            Conventions = conventions,
        });

        return new RouteHandlerBuilder(conventions);
    }

    public override IReadOnlyList<RouteEndpoint> Endpoints
    {
        get
        {
            var endpoints = new RouteEndpoint[_routeEntries.Count];
            for (int i = 0; i < _routeEntries.Count; i++)
            {
                endpoints[i] = (RouteEndpoint)CreateRouteEndpointBuilder(_routeEntries[i]).Build();
            }
            return endpoints;
        }
    }

    public override IReadOnlyList<RouteEndpoint> GetGroupedEndpoints(RouteGroupContext context)
    {
        var endpoints = new RouteEndpoint[_routeEntries.Count];
        for (int i = 0; i < _routeEntries.Count; i++)
        {
            endpoints[i] = (RouteEndpoint)CreateRouteEndpointBuilder(_routeEntries[i], context.Prefix, context.Conventions).Build();
        }
        return endpoints;
    }

    public override IChangeToken GetChangeToken() => NullChangeToken.Singleton;

    // For testing
    internal RouteEndpointBuilder GetSingleRouteEndpointBuilder()
    {
        if (_routeEntries.Count is not 1)
        {
            throw new InvalidOperationException($"There are {_routeEntries.Count} endpoints defined! This can only be called for a single endpoint.");
        }

        return CreateRouteEndpointBuilder(_routeEntries[0]);
    }

    [UnconditionalSuppressMessage("Trimmer", "IL2026",
        Justification = "We surface a RequireUnreferencedCode in the call to the Map method adding this EndpointDataSource. The trimmer is unable to infer this.")]
    private RouteEndpointBuilder CreateRouteEndpointBuilder(
        RouteEntry entry, RoutePattern? groupPrefix = null, IReadOnlyList<Action<EndpointBuilder>>? groupConventions = null)
    {
        var pattern = RoutePatternFactory.Combine(groupPrefix, entry.RoutePattern);
        var handler = entry.RouteHandler;
        var isRouteHandler = (entry.RouteAttributes & RouteAttributes.RouteHandler) == RouteAttributes.RouteHandler;
        var isFallback = (entry.RouteAttributes & RouteAttributes.Fallback) == RouteAttributes.Fallback;

        // The Map methods don't support customizing the order apart from using int.MaxValue to give MapFallback the lowest priority.
        // Otherwise, we always use the default of 0 unless a convention changes it later.
        var order = isFallback ? int.MaxValue : 0;
        var displayName = pattern.RawText ?? pattern.DebuggerToString();

        // Don't include the method name for non-route-handlers because the name is just "Invoke" when built from
        // ApplicationBuilder.Build(). This was observed in MapSignalRTests and is not very useful. Maybe if we come up
        // with a better heuristic for what a useful method name is, we could use it for everything. Inline lambdas are
        // compiler generated methods so they are filtered out even for route handlers.
        if (isRouteHandler && TypeHelper.TryGetNonCompilerGeneratedMethodName(handler.Method, out var methodName))
        {
            displayName = $"{displayName} => {methodName}";
        }

        if (entry.HttpMethods is not null)
        {
            // Prepends the HTTP method to the DisplayName produced with pattern + method name
            displayName = $"HTTP: {string.Join(", ", entry.HttpMethods)} {displayName}";
        }

        if (isFallback)
        {
            displayName = $"Fallback {displayName}";
        }

        // If we're not a route handler, we started with a fully realized (although unfiltered) RequestDelegate, so we can just redirect to that
        // while running any conventions. We'll put the original back if it remains unfiltered right before building the endpoint.
        RequestDelegate? factoryCreatedRequestDelegate = isRouteHandler ? null : (RequestDelegate)entry.RouteHandler;

        // Let existing conventions capture and call into builder.RequestDelegate as long as they do so after it has been created.
        RequestDelegate redirectRequestDelegate = context =>
        {
            if (factoryCreatedRequestDelegate is null)
            {
                throw new InvalidOperationException(Resources.RouteEndpointDataSource_RequestDelegateCannotBeCalledBeforeBuild);
            }

            return factoryCreatedRequestDelegate(context);
        };

        // Add MethodInfo and HttpMethodMetadata (if any) as first metadata items as they are intrinsic to the route much like
        // the pattern or default display name. This gives visibility to conventions like WithOpenApi() to intrinsic route details
        // (namely the MethodInfo) even when applied early as group conventions.
        RouteEndpointBuilder builder = new(redirectRequestDelegate, pattern, order)
        {
            DisplayName = displayName,
            ApplicationServices = _applicationServices,
            Metadata = { handler.Method },
        };

        if (entry.HttpMethods is not null)
        {
            builder.Metadata.Add(new HttpMethodMetadata(entry.HttpMethods));
        }

        // Apply group conventions before entry-specific conventions added to the RouteHandlerBuilder.
        if (groupConventions is not null)
        {
            foreach (var groupConvention in groupConventions)
            {
                groupConvention(builder);
            }
        }

        // Add delegate attributes as metadata before entry-specific conventions but after group conventions.
        var attributes = handler.Method.GetCustomAttributes();
        if (attributes is not null)
        {
            foreach (var attribute in attributes)
            {
                builder.Metadata.Add(attribute);
            }
        }

        entry.Conventions.IsReadonly = true;
        foreach (var entrySpecificConvention in entry.Conventions)
        {
            entrySpecificConvention(builder);
        }

        if (isRouteHandler || builder.FilterFactories.Count > 0)
        {
            var routeParamNames = new List<string>(pattern.Parameters.Count);
            foreach (var parameter in pattern.Parameters)
            {
                routeParamNames.Add(parameter.Name);
            }

            RequestDelegateFactoryOptions factoryOptions = new()
            {
                ServiceProvider = _applicationServices,
                RouteParameterNames = routeParamNames,
                ThrowOnBadRequest = _throwOnBadRequest,
                DisableInferBodyFromParameters = ShouldDisableInferredBodyParameters(entry.HttpMethods),
                EndpointMetadata = builder.Metadata,
                EndpointFilterFactories = builder.FilterFactories.AsReadOnly(),
            };

            // We ignore the returned EndpointMetadata has been already populated since we passed in non-null EndpointMetadata.
            factoryCreatedRequestDelegate = RequestDelegateFactory.Create(entry.RouteHandler, factoryOptions).RequestDelegate;
        }

        if (ReferenceEquals(builder.RequestDelegate, redirectRequestDelegate))
        {
            // No convention has changed builder.RequestDelegate, so we can just replace it with the final version as an optimization.
            // We still set factoryRequestDelegate in case something is still referencing the redirected version of the RequestDelegate.
            builder.RequestDelegate = factoryCreatedRequestDelegate;
        }

        return builder;
    }

    private static bool ShouldDisableInferredBodyParameters(IEnumerable<string>? httpMethods)
    {
        static bool ShouldDisableInferredBodyForMethod(string method) =>
            // GET, DELETE, HEAD, CONNECT, TRACE, and OPTIONS normally do not contain bodies
            method.Equals(HttpMethods.Get, StringComparison.Ordinal) ||
            method.Equals(HttpMethods.Delete, StringComparison.Ordinal) ||
            method.Equals(HttpMethods.Head, StringComparison.Ordinal) ||
            method.Equals(HttpMethods.Options, StringComparison.Ordinal) ||
            method.Equals(HttpMethods.Trace, StringComparison.Ordinal) ||
            method.Equals(HttpMethods.Connect, StringComparison.Ordinal);

        // If the endpoint accepts any kind of request, we should still infer parameters can come from the body.
        if (httpMethods is null)
        {
            return false;
        }

        foreach (var method in httpMethods)
        {
            if (ShouldDisableInferredBodyForMethod(method))
            {
                // If the route handler was mapped explicitly to handle an HTTP method that does not normally have a request body,
                // we assume any invocation of the handler will not have a request body no matter what other HTTP methods it may support.
                return true;
            }
        }

        return false;
    }

    private struct RouteEntry
    {
        public RoutePattern RoutePattern { get; init; }
        public Delegate RouteHandler { get; init; }
        public IEnumerable<string>? HttpMethods { get; init; }
        public RouteAttributes RouteAttributes { get; init; }
        public ThrowOnAddAfterEndpointBuiltConventionCollection Conventions { get; init; }
    }

    [Flags]
    private enum RouteAttributes
    {
        // The endpoint was defined by a RequestDelegate, RequestDelegateFactory.Create() should be skipped unless there are endpoint filters.
        None = 0,
        // This was added as Delegate route handler, so RequestDelegateFactory.Create() should always be called.
        RouteHandler = 1,
        // This was added by MapFallback.
        Fallback = 2,
    }

    // This private class is only exposed to internal code via ICollection<Action<EndpointBuilder>> in RouteEndpointBuilder where only Add is called.
    private sealed class ThrowOnAddAfterEndpointBuiltConventionCollection : List<Action<EndpointBuilder>>, ICollection<Action<EndpointBuilder>>
    {
        // We throw if someone tries to add conventions to the RouteEntry after endpoints have already been resolved meaning the conventions
        // will not be observed given RouteEndpointDataSource is not meant to be dynamic and uses NullChangeToken.Singleton.
        public bool IsReadonly { get; set; }

        void ICollection<Action<EndpointBuilder>>.Add(Action<EndpointBuilder> convention)
        {
            if (IsReadonly)
            {
                throw new InvalidOperationException(Resources.RouteEndpointDataSource_ConventionsCannotBeModifiedAfterBuild);
            }

            Add(convention);
        }
    }
}
