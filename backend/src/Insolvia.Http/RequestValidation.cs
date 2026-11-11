using Insolvia.Domain.Fields;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Insolvia.Http;

/// <summary>
/// A route's request record is checked before its handler runs
/// (<see cref="RecordValidator"/>): a failure is a 400 <c>ValidationError</c>
/// naming every field. A body the reader cannot bind to the record is the
/// same error, keyed by the path it stopped at (<see cref="ErrorResponses.ForBadRequest"/>).
/// </summary>
public static class RequestValidation
{
    /// <summary>Lets an unreadable body reach the error handler, which names the field.</summary>
    /// <param name="services">The host's services.</param>
    /// <returns>The same services.</returns>
    public static IServiceCollection AddRequestValidation(this IServiceCollection services) =>
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

    /// <summary>Checks every request record the group's handlers take.</summary>
    /// <typeparam name="TBuilder">The group.</typeparam>
    /// <param name="builder">The routes.</param>
    /// <returns>The same routes.</returns>
    public static TBuilder ValidatesRequests<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilterFactory((factory, next) =>
        {
            // The parameter the framework reads the body into: one of our records, not a service.
            var services = factory.ApplicationServices.GetRequiredService<IServiceProviderIsService>();
            var records = factory.MethodInfo.GetParameters()
                .Select((parameter, index) => (parameter, index))
                .Where(p => p.parameter.ParameterType is { IsClass: true, IsAbstract: false } type
                    && type.Namespace?.StartsWith("Insolvia", StringComparison.Ordinal) == true
                    && !services.IsService(type))
                .Select(p => p.index)
                .ToArray();
            if (records.Length == 0)
            {
                return next;
            }

            return invocation =>
            {
                var options = invocation.HttpContext.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
                foreach (var index in records)
                {
                    if (invocation.Arguments[index] is { } record)
                    {
                        RecordValidator.ThrowIfInvalid(record, options);
                    }
                }

                return next(invocation);
            };
        });
        return builder;
    }
}
