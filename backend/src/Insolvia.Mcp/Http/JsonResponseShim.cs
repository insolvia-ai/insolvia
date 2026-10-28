using System.IO.Pipelines;
using System.Text;
using Microsoft.AspNetCore.Http.Features;

namespace Insolvia.Mcp.Http;

/// <summary>
/// The JSON-response mode the C# SDK lacks (research 2026-10-27 § 2.4, gap 1).
/// The Python server runs <c>json_response=True</c>; SDK 2.3.0 answers every
/// POST as a <c>text/event-stream</c> carrying one <c>data:</c> event. In
/// stateless mode with no server-to-client requests that stream always holds
/// exactly one JSON-RPC message, so this buffers the body and re-emits that
/// message as <c>application/json</c>. Anything else passes through untouched.
/// </summary>
public sealed class JsonResponseShim(RequestDelegate next)
{
    /// <summary>Runs the transport into a buffer, then rewrites a one-event stream as JSON.</summary>
    /// <param name="context">The request.</param>
    /// <returns>A task.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var original = context.Features.Get<IHttpResponseBodyFeature>()!;
        using var buffer = new MemoryStream();
        var feature = new BufferingBodyFeature(original, buffer);
        context.Features.Set<IHttpResponseBodyFeature>(feature);
        try
        {
            await next(context).ConfigureAwait(false);
            await feature.Writer.FlushAsync(context.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            context.Features.Set(original);
        }

        var response = context.Response;
        var isStream = response.ContentType?.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) == true;
        if (isStream && response.StatusCode == StatusCodes.Status200OK)
        {
            var text = Encoding.UTF8.GetString(buffer.ToArray());
            var data = text.Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                .Select(line => line[5..].TrimStart())
                .ToList();
            if (data.Count == 1)
            {
                var json = Encoding.UTF8.GetBytes(data[0]);
                response.ContentType = "application/json";
                response.Headers.CacheControl = default;
                response.Headers.Remove("X-Accel-Buffering");
                response.ContentLength = json.Length;
                await original.Stream.WriteAsync(json, context.RequestAborted).ConfigureAwait(false);
                return;
            }
        }

        if (buffer.Length > 0)
        {
            response.ContentLength = buffer.Length;
            buffer.Position = 0;
            await buffer.CopyToAsync(original.Stream, context.RequestAborted).ConfigureAwait(false);
        }
    }

    private sealed class BufferingBodyFeature(IHttpResponseBodyFeature prior, Stream buffer) : IHttpResponseBodyFeature
    {
        public Stream Stream => buffer;

        public PipeWriter Writer { get; } = PipeWriter.Create(buffer, new StreamPipeWriterOptions(leaveOpen: true));

        public void DisableBuffering()
        {
        }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default) =>
            prior.SendFileAsync(path, offset, count, cancellationToken);

        public async Task CompleteAsync() => await Writer.FlushAsync().ConfigureAwait(false);
    }
}
