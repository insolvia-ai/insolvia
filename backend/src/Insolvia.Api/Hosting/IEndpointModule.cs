namespace Insolvia.Api.Hosting;

/// <summary>
/// A set of routes registered in DI and mapped inside the API's pipeline —
/// so they get the same CORS, request log, error mapping and method fallback
/// as every other route. Program.cs maps every registered module after
/// <c>/health</c>; the route tests register theirs (the 401/403 matrix) this
/// way, without a test route ever existing in the production binary.
/// </summary>
public interface IEndpointModule
{
    /// <summary>Maps the module's routes.</summary>
    /// <param name="routes">Where to map them.</param>
    void Map(IEndpointRouteBuilder routes);
}
