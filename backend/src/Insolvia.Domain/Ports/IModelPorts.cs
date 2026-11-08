using System.Text.Json.Nodes;

namespace Insolvia.Core.Ports;

/// <summary>What the review model answers: which model ran, and the raw structured output (still to be validated).</summary>
public sealed record ReviewModelResult(string Model, JsonObject Raw);

/// <summary>
/// What the extraction model is shown: the document's bytes and the fixed
/// per-kind instruction pair. Nothing else — no case data reaches the prompt.
/// </summary>
public sealed record ExtractionRequest(string MediaType, ReadOnlyMemory<byte> Data, string SystemPrompt, JsonObject Schema);

/// <summary>What the extraction model answers: which model ran, and the raw structured output.</summary>
public sealed record ExtractionModelResult(string Model, JsonObject Raw);

/// <summary>
/// Runs the AI petition review's model call (Python's <c>ReviewModel</c>; ADR
/// 0019). Failure contract: a failure a retry cannot change (a rejected key, a
/// refusal) is a <see cref="Jobs.JobException"/>; anything transient propagates
/// so SQS redelivery retries the job.
/// </summary>
public interface IReviewModel
{
    /// <summary>Review one already-scrubbed document.</summary>
    Task<ReviewModelResult> ReviewAsync(string document, CancellationToken cancellationToken = default);
}

/// <summary>Runs one document-extraction model call (Python's <c>ExtractionModel</c>) — the review's failure contract.</summary>
public interface IExtractionModel
{
    /// <summary>Extract one document.</summary>
    Task<ExtractionModelResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken = default);
}
