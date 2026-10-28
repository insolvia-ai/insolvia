using System.Net.Sockets;
using Insolvia.Filing.Fence;

namespace Insolvia.Filing.Http;

/// <summary>One request to a court.</summary>
/// <param name="Method">GET or POST.</param>
/// <param name="Url">Absolute.</param>
/// <param name="Body">The body, or null.</param>
/// <param name="ContentType">The body's type, or null.</param>
public sealed record CourtRequest(string Method, string Url, byte[]? Body = null, string? ContentType = null);

/// <summary>One court answer. <see cref="Url"/> is the request's, so a redirect resolves against it.</summary>
/// <param name="Status">The HTTP status.</param>
/// <param name="Headers">Response headers (first value per name).</param>
/// <param name="Body">The bytes.</param>
/// <param name="Url">The URL asked for.</param>
public sealed record CourtResponse(int Status, IReadOnlyDictionary<string, string> Headers, byte[] Body, string Url)
{
    /// <summary>A header, case-insensitively.</summary>
    public string? Header(string name) =>
        Headers.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>The body as UTF-8, replacement characters for anything else.</summary>
    public string Text => System.Text.Encoding.UTF8.GetString(Body);
}

/// <summary>The court did not answer within the request timeout.</summary>
public sealed class CourtTimeoutException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public CourtTimeoutException()
        : base("the court did not answer in time")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public CourtTimeoutException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public CourtTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The connection failed (refused, reset, TLS). Carries no request data.</summary>
public sealed class CourtTransportException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public CourtTransportException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public CourtTransportException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public CourtTransportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// One court session's HTTP. An implementation MUST pass every URL through
/// the environment's fence before connecting, MUST NOT follow redirects
/// itself (the driver follows each hop through <see cref="SendAsync"/>, so
/// the fence sees it), and keeps the session's cookies.
/// </summary>
public interface ICourtHttpClient
{
    /// <summary>Send one request.</summary>
    Task<CourtResponse> SendAsync(CourtRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// THE ONLY HTTP the filing worker has toward a court (from
/// <c>adapters/http/fenced_client.py</c>). The fence is checked BEFORE a
/// request message exists: a refused host throws
/// <see cref="HostNotAllowedException"/>, and no handler is called, no DNS
/// lookup made, no socket opened (FenceTests proves it with a handler that
/// fails the test if reached). The default handler checks the fence AGAIN in
/// its connect callback — the socket-level wall. Redirects are never
/// followed here; cookies are this run's alone, and nothing is logged.
/// </summary>
public sealed class FencedHttpClient : ICourtHttpClient, IDisposable
{
    private readonly HostFence _fence;
    private readonly HttpMessageInvoker _invoker;
    private readonly TimeSpan _timeout;
    private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);

    /// <summary>Creates a client for one run.</summary>
    /// <param name="fence">The environment's fence.</param>
    /// <param name="timeout">One request's limit.</param>
    /// <param name="handler">The transport — a test's stand-in; the fenced sockets handler when null.</param>
    public FencedHttpClient(HostFence fence, TimeSpan timeout, HttpMessageHandler? handler = null)
    {
        _fence = fence ?? throw new ArgumentNullException(nameof(fence));
        _timeout = timeout;
        _invoker = new HttpMessageInvoker(handler ?? SocketsFor(fence), disposeHandler: true);
    }

    /// <summary>The sockets handler: no redirects, no cookie container, no proxy, and the fence at connect time.</summary>
    public static SocketsHttpHandler SocketsFor(HostFence fence)
    {
        ArgumentNullException.ThrowIfNull(fence);
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            ConnectCallback = async (context, cancellationToken) =>
            {
                // The second wall: the endpoint about to be dialled, checked
                // against the same allowlist before the socket exists.
                var endpoint = context.DnsEndPoint;
                var scheme = context.InitialRequestMessage.RequestUri?.Scheme ?? Uri.UriSchemeHttps;
                var host = endpoint.Host.Contains(':', StringComparison.Ordinal) ? $"[{endpoint.Host}]" : endpoint.Host;
                fence.Check($"{scheme}://{host}:{endpoint.Port}/");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
    }

    /// <inheritdoc />
    public async Task<CourtResponse> SendAsync(CourtRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // FIRST, before anything that could touch the network.
        _fence.Check(request.Url);
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
        if (request.Body is not null)
        {
            message.Content = new ByteArrayContent(request.Body);
            if (request.ContentType is not null)
            {
                message.Content.Headers.TryAddWithoutValidation("Content-Type", request.ContentType);
            }
        }

        if (_cookies.Count > 0)
        {
            message.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", _cookies.Select(pair => $"{pair.Key}={pair.Value}")));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            using var response = await _invoker.SendAsync(message, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
            {
                headers.TryAdd(name, values.First());
                if (string.Equals(name, "Set-Cookie", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var value in values)
                    {
                        var pair = value.Split(';', 2)[0];
                        var equals = pair.IndexOf('=', StringComparison.Ordinal);
                        if (equals > 0)
                        {
                            _cookies[pair[..equals].Trim()] = pair[(equals + 1)..].Trim();
                        }
                    }
                }
            }

            return new CourtResponse((int)response.StatusCode, headers, body, request.Url);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CourtTimeoutException("the court did not answer in time", error);
        }
        catch (HttpRequestException error) when (error.InnerException is HostNotAllowedException refused)
        {
            throw refused;
        }
        catch (HttpRequestException error)
        {
            throw new CourtTransportException(error.HttpRequestError.ToString(), error);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _invoker.Dispose();
}
