using System.Text.Json;
using System.Text.Json.Nodes;
using Insolvia.Core;
using Insolvia.Core.Access;
using Insolvia.Core.Ports;
using Insolvia.Mcp.Auth;
using Insolvia.Mcp.Logging;
using Insolvia.Mcp.Views;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Insolvia.Mcp.Tools;

/// <summary>What a tool body is handed: the validated arguments and the per-call seams.</summary>
/// <param name="Arguments">The arguments, validated as pydantic would have.</param>
/// <param name="Tools">The domain layer.</param>
/// <param name="Subject">The verified token's subject.</param>
/// <param name="ClientId">The verified token's app client — a candidate's origin.</param>
/// <param name="Resolve">Resolves the caller's accessor (null when they have no active firm), uncached.</param>
/// <param name="CancellationToken">Cancels the call.</param>
public sealed record ToolCall(
    ToolArguments Arguments,
    CaseTools Tools,
    string Subject,
    string ClientId,
    Func<Task<Accessor?>> Resolve,
    CancellationToken CancellationToken)
{
    /// <summary>
    /// The caller's accessor, or the one-message refusal the API's
    /// <c>current_accessor</c> raises: "you were disabled", "you were never
    /// added" and "your firm is suspended" are the same instruction.
    /// </summary>
    /// <returns>The accessor.</returns>
    public async Task<Accessor> AccessorOrRefuseAsync() =>
        await Resolve().ConfigureAwait(false) ?? throw new ForbiddenException("your account is not active in a firm");
}

/// <summary>
/// One tool of the surface: its listing entry (name, title, description,
/// annotations, the pydantic-shaped schemas) and its call, wrapped in the
/// error mapping of services/mcp <c>api/results.py</c>.
/// </summary>
/// <remarks>
/// A subclass of the SDK's <see cref="McpServerTool"/> rather than an
/// attributed method because the listing is a contract: the schemas must be
/// the ones the Python server published (the SDK would derive its own from a
/// C# signature), and <c>isError</c> must be ours to set. The accessor is
/// resolved INSIDE each call, from the store, every time (ADR 0009).
/// </remarks>
public sealed class InsolviaTool : McpServerTool
{
    private readonly IReadOnlyList<ToolParameter> _parameters;
    private readonly Func<ToolCall, Task<JsonObject>> _body;

    /// <summary>Defines a tool.</summary>
    /// <param name="name">The wire name.</param>
    /// <param name="title">The display title.</param>
    /// <param name="description">What a harness's model reads.</param>
    /// <param name="readOnly">Read-only (<c>readOnlyHint: true</c>) or a non-destructive write.</param>
    /// <param name="parameters">The parameters, in signature order.</param>
    /// <param name="outputSchema">The result's schema.</param>
    /// <param name="body">The call.</param>
    public InsolviaTool(
        string name,
        string title,
        string description,
        bool readOnly,
        IReadOnlyList<ToolParameter> parameters,
        JsonObject outputSchema,
        Func<ToolCall, Task<JsonObject>> body)
    {
        _parameters = parameters;
        _body = body;
        ProtocolTool = new Tool
        {
            Name = name,
            Title = title,
            Description = description,
            InputSchema = JsonSerializer.SerializeToElement(ToolSchemas.Input(name, parameters)),
            OutputSchema = JsonSerializer.SerializeToElement(outputSchema),

            // Nothing on this surface is destructive — a proposal is additive
            // and a withdrawal retracts the caller's own pending row — so a
            // write says so explicitly, and a read says only that it reads.
            Annotations = readOnly
                ? new ToolAnnotations { ReadOnlyHint = true }
                : new ToolAnnotations { ReadOnlyHint = false, DestructiveHint = false },
        };
    }

    /// <summary>The four domain codes and <c>internal</c> (mcp-surface.md § Error vocabulary).</summary>
    public static class Codes
    {
        /// <summary>A bad argument, a bad cursor, an oversized batch.</summary>
        public const string ValidationFailed = "validation_failed";

        /// <summary>Does not exist, or is not the caller's to know — one answer, on purpose.</summary>
        public const string NotFound = "not_found";

        /// <summary>A fact about the caller's own account.</summary>
        public const string PermissionDenied = "permission_denied";

        /// <summary>The resource exists, but its state refuses.</summary>
        public const string Conflict = "conflict";

        /// <summary>Anything unexpected — no detail.</summary>
        public const string Internal = "internal";
    }

    /// <inheritdoc />
    public override Tool ProtocolTool { get; }

    /// <inheritdoc />
    public override IReadOnlyList<object> Metadata { get; } = [];

    /// <summary>
    /// A successful result: the payload as <c>structuredContent</c>, and its
    /// <c>json.dumps</c> as the text block older harnesses read.
    /// </summary>
    /// <param name="payload">The result object.</param>
    /// <returns>The result.</returns>
    public static CallToolResult Success(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = PyJson.Dumps(payload) }],
            StructuredContent = JsonSerializer.SerializeToElement(payload),
            IsError = false,
        };
    }

    /// <summary>A domain refusal: <c>{error: {code, message, fields?}}</c>, <c>isError: true</c>.</summary>
    /// <param name="code">One of <see cref="Codes"/>.</param>
    /// <param name="message">The message.</param>
    /// <param name="fields">Per-field messages, keyed by path.</param>
    /// <returns>The result.</returns>
    public static CallToolResult Refusal(string code, string message, IReadOnlyDictionary<string, string>? fields = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (fields is not null)
        {
            var map = new JsonObject();
            foreach (var (path, text) in fields)
            {
                map[path] = text;
            }

            error["fields"] = map;
        }

        var body = new JsonObject { ["error"] = error };
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = PyJson.Dumps(body) }],
            StructuredContent = JsonSerializer.SerializeToElement(body),
            IsError = true,
        };
    }

    /// <summary>
    /// Core's exception classes mapped to codes — specific before general, as
    /// the API's handlers register: a field error IS a validation error.
    /// </summary>
    /// <param name="error">The domain refusal.</param>
    /// <returns>The result.</returns>
    public static CallToolResult Map(ApiException error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error switch
        {
            FieldValidationException field => Refusal(Codes.ValidationFailed, field.Message, field.Fields),
            ValidationException => Refusal(Codes.ValidationFailed, error.Message),
            NotFoundException => Refusal(Codes.NotFound, error.Message),
            ForbiddenException => Refusal(Codes.PermissionDenied, error.Message),
            ConflictException => Refusal(Codes.Conflict, error.Message),
            _ => Refusal(Codes.ValidationFailed, error.Message),
        };
    }

    /// <inheritdoc />
    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = ProtocolTool.Name;
        var services = request.Services ?? throw new InvalidOperationException("the MCP request has no service provider");
        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger(StructuredLog.Categories.Results);

        JsonObject? arguments = null;
        if (request.Params?.Arguments is { } raw)
        {
            arguments = [];
            foreach (var (key, value) in raw)
            {
                arguments[key] = JsonNode.Parse(value.GetRawText());
            }
        }

        // The SDK's own validation of the signature, before the tool runs:
        // a text-only isError, not the domain envelope.
        if (!ToolArguments.TryParse(name, _parameters, arguments, out var parsed, out var invalid))
        {
            log.Info("tool rejected arguments", ("tool", name));
            return new CallToolResult { Content = [new TextContentBlock { Text = invalid! }], IsError = true };
        }

        try
        {
            // The auth scheme refuses unauthenticated requests before any tool
            // runs, so a missing subject here is a composition bug, not a
            // caller error — surfaced as `internal` below.
            var user = request.User;
            var subject = user?.FindFirst(BearerAuthentication.SubjectClaim)?.Value;
            if (string.IsNullOrEmpty(subject))
            {
                throw new InvalidOperationException("tool invoked without an authenticated token");
            }

            var clientId = user!.FindFirst(BearerAuthentication.ClientIdClaim)?.Value ?? string.Empty;
            var firms = services.GetRequiredService<IFirmStore>();
            var authLog = services.GetRequiredService<ILoggerFactory>().CreateLogger(StructuredLog.Categories.Auth);
            var call = new ToolCall(
                parsed,
                services.GetRequiredService<CaseTools>(),
                subject,
                clientId,
                () => AccessorResolution.ResolveAsync(firms, subject, authLog, cancellationToken),
                cancellationToken);
            return Success(await _body(call).ConfigureAwait(false));
        }
        catch (ApiException error)
        {
            log.Info("tool refused", ("tool", name), ("kind", error.GetType().Name));
            return Map(error);
        }
#pragma warning disable CA1031 // The tool boundary: anything unexpected is `internal`, never a crash or a leaked detail.
        catch (Exception error) when (error is not OperationCanceledException)
#pragma warning restore CA1031
        {
            // Metadata only — the tool name; never the arguments.
            log.Write(LogLevel.Error, "unexpected MCP tool failure", error, ("tool", name));
            return Refusal(Codes.Internal, "request failed");
        }
    }
}
