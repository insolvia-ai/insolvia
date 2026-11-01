using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Insolvia.Core.Jobs;
using Insolvia.Core.Ports;
using Insolvia.Worker.Workers;

namespace Insolvia.Worker.Anthropic;

/// <summary>
/// The model policy both adapters share (ADR 0019; review_model.py): the model
/// id lives HERE, once. Opus-tier deliberately — the review's value is subtle
/// cross-schedule inconsistencies, and the tier stays eligible for a
/// zero-data-retention agreement. 240 s per attempt and ONE SDK retry keep the
/// worst case (~2 × 240 s plus backoff) inside the worker Lambda's 900 s, so a
/// failure row is written rather than the attempt being killed mid-flight.
/// </summary>
public static class AnthropicPolicy
{
    /// <summary>The model (ADR 0019 pins it; moving is an ADR decision, not a port detail).</summary>
    public const string Model = "claude-opus-5";

    /// <summary>The output ceiling: bounds a runaway generation, not a legitimate answer.</summary>
    public const long MaxOutputTokens = 16000;

    /// <summary>The fixed user-turn instruction beside an extraction's document.</summary>
    public const string ExtractionInstruction = "Extract this document into the required output format, following your instructions exactly.";

    /// <summary>The per-attempt budget. C#'s Timeout bounds the WHOLE call (body included), per attempt.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(240);

    /// <summary>One SDK retry.</summary>
    public const int MaxRetries = 1;

    /// <summary>The client every adapter uses. <paramref name="http"/> is the test seam — nothing else changes.</summary>
    /// <remarks>
    /// The default <see cref="HttpClient"/> has NO timeout of its own: its
    /// built-in 100 s would cancel a call well inside <see cref="Timeout"/>,
    /// which the SDK applies per attempt (its docs warn of exactly this).
    /// </remarks>
    public static AnthropicClient NewClient(string apiKey, HttpClient? http = null) => new()
    {
        ApiKey = apiKey,
        Timeout = Timeout,
        MaxRetries = MaxRetries,
        HttpClient = http ?? new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan },
    };

    /// <summary>
    /// The review's request. No sampling parameters, ever: the SDK's
    /// <c>anthropic-version</c> (2026-10-21) refuses <c>temperature</c>/<c>top_p</c>/<c>top_k</c>.
    /// </summary>
    public static MessageCreateParams ReviewRequest(string document, string model = Model) => new()
    {
        Model = model,
        MaxTokens = MaxOutputTokens,
        System = PetitionReview.SystemPrompt,
        Messages = [new UserMessageParam { Content = document }],
        OutputConfig = JsonSchemaOutput(PetitionReview.OutputSchema()),
    };

    /// <summary>An extraction's request: the document (PDF) or image block, base64, BEFORE the instruction.</summary>
    public static MessageCreateParams ExtractionRequest(ExtractionRequest request, string model = Model)
    {
        ArgumentNullException.ThrowIfNull(request);
        var encoded = Convert.ToBase64String(request.Data.Span);
        ContentBlockParam block = request.MediaType == "application/pdf"
            ? new DocumentBlockParam { Source = new Base64PdfSource { Data = encoded } }
            : new ImageBlockParam
            {
                Source = new Base64ImageSource
                {
                    Data = encoded,
                    MediaType = request.MediaType switch
                    {
                        "image/png" => MediaType.ImagePng,
                        "image/jpeg" => MediaType.ImageJpeg,
                        _ => throw new ArgumentException($"the model API does not read {request.MediaType}", nameof(request)),
                    },
                },
            };
        return new MessageCreateParams
        {
            Model = model,
            MaxTokens = MaxOutputTokens,
            System = request.SystemPrompt,
            Messages = [new UserMessageParam { Content = new List<ContentBlockParam> { block, new TextBlockParam { Text = ExtractionInstruction } } }],
            OutputConfig = JsonSchemaOutput(request.Schema),
        };
    }

    /// <summary>The wire value of an SDK enum or union (its JSON string).</summary>
    internal static string? Wire(object? value) => value?.ToString()?.Trim('"');

    /// <summary>The first text block's text, or null.</summary>
    internal static string? FirstText(Message response)
    {
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out var text))
            {
                return text.Text;
            }
        }

        return null;
    }

    /// <summary>Metadata only (GLBA): token counts and identifiers, never content.</summary>
    internal static void LogCall(JobLog log, string message, Message response) =>
        log.Info(
            message,
            ("model", Wire(response.Model)),
            ("stop_reason", Wire(response.StopReason)),
            ("input_tokens", response.Usage.InputTokens),
            ("output_tokens", response.Usage.OutputTokens));

    /// <summary>The answer text as the raw structured output; malformed is infrastructure (a fresh generation may differ).</summary>
    internal static JsonObject ParseAnswer(string text) =>
        JsonNode.Parse(text) as JsonObject ?? throw new FormatException("the model's structured output is not an object");

    // output_config.format = {type: json_schema, schema}.
    private static OutputConfig JsonSchemaOutput(JsonObject schema)
    {
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(schema.ToJsonString());
        foreach (var property in document.RootElement.EnumerateObject())
        {
            properties[property.Name] = property.Value.Clone();
        }

        return new OutputConfig { Format = new JsonOutputFormat { Schema = properties } };
    }
}

/// <summary>
/// <see cref="IReviewModel"/> over the Messages API (review_model.py). A failure a
/// retry cannot change (a rejected key, a refusal) is a <see cref="JobException"/>;
/// anything transient propagates so SQS redelivery retries the job.
/// </summary>
/// <param name="client">The configured client (<see cref="AnthropicPolicy.NewClient"/>).</param>
/// <param name="log">The log.</param>
/// <param name="model">The model id.</param>
public sealed class AnthropicReviewModel(AnthropicClient client, JobLog? log = null, string model = AnthropicPolicy.Model) : IReviewModel
{
    private readonly JobLog _log = log ?? JobLog.Console;

    /// <inheritdoc />
    public async Task<ReviewModelResult> ReviewAsync(string document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        Message response;
        try
        {
            response = await client.Messages.Create(AnthropicPolicy.ReviewRequest(document, model), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is AnthropicUnauthorizedException or AnthropicForbiddenException)
        {
            // A key that does not work will not start working on redelivery.
            _log.Error("anthropic credentials rejected", ("exception", error.GetType().Name));
            throw new JobException("AI review is not configured correctly in this environment.", "not_configured", error);
        }

        AnthropicPolicy.LogCall(_log, "review model call finished", response);
        var stop = AnthropicPolicy.Wire(response.StopReason);
        if (stop == "refusal")
        {
            throw new JobException("The review model declined to review this case.", "model_refused");
        }

        var text = AnthropicPolicy.FirstText(response);
        if (stop != "end_turn" || text is null)
        {
            // max_tokens or another surprise mid-generation — a fresh generation differs.
            throw new InvalidOperationException($"review model stopped unexpectedly ({stop})");
        }

        return new ReviewModelResult(AnthropicPolicy.Wire(response.Model) ?? model, AnthropicPolicy.ParseAnswer(text));
    }
}

/// <summary>
/// <see cref="IExtractionModel"/> over the Messages API (extraction_model.py) —
/// the review's shape, except that a <c>max_tokens</c> stop is DETERMINISTIC
/// here (the same document overflows the same ceiling) and so a <see cref="JobException"/>.
/// </summary>
/// <param name="client">The configured client.</param>
/// <param name="log">The log.</param>
/// <param name="model">The model id.</param>
public sealed class AnthropicExtractionModel(AnthropicClient client, JobLog? log = null, string model = AnthropicPolicy.Model) : IExtractionModel
{
    private readonly JobLog _log = log ?? JobLog.Console;

    /// <inheritdoc />
    public async Task<ExtractionModelResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        Message response;
        try
        {
            response = await client.Messages.Create(AnthropicPolicy.ExtractionRequest(request, model), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is AnthropicUnauthorizedException or AnthropicForbiddenException)
        {
            _log.Error("anthropic credentials rejected", ("exception", error.GetType().Name));
            throw new JobException("AI extraction is not configured correctly in this environment.", "not_configured", error);
        }

        AnthropicPolicy.LogCall(_log, "extraction model call finished", response);
        var stop = AnthropicPolicy.Wire(response.StopReason);
        if (stop == "refusal")
        {
            throw new JobException("The extraction model declined to read this document.", "model_refused");
        }

        if (stop == "max_tokens")
        {
            throw new JobException("This document produced more records than one extraction can hold — split it and try again.", "too_dense");
        }

        var text = AnthropicPolicy.FirstText(response);
        if (stop != "end_turn" || text is null)
        {
            throw new InvalidOperationException($"extraction model stopped unexpectedly ({stop})");
        }

        return new ExtractionModelResult(AnthropicPolicy.Wire(response.Model) ?? model, AnthropicPolicy.ParseAnswer(text));
    }
}
