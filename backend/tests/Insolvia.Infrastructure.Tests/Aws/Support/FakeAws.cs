using System.Reflection;
using Amazon.Runtime;

namespace Insolvia.Aws.Tests.Support;

/// <summary>
/// An AWS SDK client interface answered by a handler, with no network: every
/// request is recorded, and a method the handler does not answer (null) fails
/// the test — which is how "this adapter only ever calls X" is pinned. The
/// Sealing/FakeKms.cs pattern, for any service interface.
/// </summary>
internal static class FakeAws
{
    public static (TClient Client, FakeAwsState State) Create<TClient>(Func<string, AmazonWebServiceRequest, object?> handler)
        where TClient : class
    {
        var client = DispatchProxy.Create<TClient, FakeAwsProxy>();
        var proxy = (FakeAwsProxy)(object)client;
        proxy.State = new FakeAwsState(handler);
        return (client, proxy.State);
    }
}

internal sealed class FakeAwsState(Func<string, AmazonWebServiceRequest, object?> handler)
{
    public List<(string Method, AmazonWebServiceRequest Request)> Calls { get; } = [];

    public object Answer(string method, AmazonWebServiceRequest request)
    {
        Calls.Add((method, request));
        return handler(method, request) ?? throw new InvalidOperationException($"the fake was asked for {method}, which this adapter should not call");
    }
}

/// <summary>The proxy behind the interface. Public with a parameterless constructor because <see cref="DispatchProxy"/> requires it.</summary>
public class FakeAwsProxy : DispatchProxy
{
    internal FakeAwsState State { get; set; } = null!;

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (targetMethod.Name == nameof(IDisposable.Dispose))
        {
            return null;
        }

        var request = args?.OfType<AmazonWebServiceRequest>().FirstOrDefault()
            ?? throw new InvalidOperationException($"the fake was asked for {targetMethod.Name} without a request object");
        object answer;
        try
        {
            answer = State.Answer(targetMethod.Name, request);
        }
        catch (Exception error) when (targetMethod.ReturnType.IsGenericType && targetMethod.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            // An SDK exception is how the service answers; surface it the way
            // the real async client does, as a faulted task.
            var fromException = typeof(Task).GetMethod(nameof(Task.FromException), 1, [typeof(Exception)])!
                .MakeGenericMethod(targetMethod.ReturnType.GetGenericArguments()[0]);
            return fromException.Invoke(null, [error]);
        }

        var fromResult = typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(targetMethod.ReturnType.GetGenericArguments()[0]);
        return fromResult.Invoke(null, [answer]);
    }
}
