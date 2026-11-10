namespace Insolvia.Api.Http;

/// <summary>Temporary: replaced at merge by Insolvia.Platform.Http.WireOpenApi.RequestBody.</summary>
public static class RequestBodyShim
{
    /// <summary>Documents the JSON body the route reads, without constraining routing.</summary>
    /// <typeparam name="TRequest">The body's shape.</typeparam>
    /// <param name="builder">The route.</param>
    /// <returns>The same route.</returns>
    public static RouteHandlerBuilder RequestBody<TRequest>(this RouteHandlerBuilder builder) => builder;
}
