using System.ComponentModel.DataAnnotations;
using Insolvia.Domain.Documents;
using Insolvia.Domain.Fields;

namespace Insolvia.Api.Portal;

/// <summary>
/// A client's upload against one of the firm's document requests
/// (<c>POST /v1/portal/documents</c>). The document's kind is the request's.
/// Every check runs before an upload URL exists.
/// </summary>
/// <param name="RequestId">The open document request this upload answers.</param>
/// <param name="FileName">The file name, at most 255 characters, no path separators or invisible characters.</param>
/// <param name="ContentType">The content type the upload will carry.</param>
/// <param name="ByteSize">The size in bytes, at most 50 MB.</param>
public sealed record PortalUploadRequest(
    [property: Required(ErrorMessage = "Choose the document this upload is for."), MinLength(1, ErrorMessage = "Choose the document this upload is for.")] string? RequestId,
    [property: Required(ErrorMessage = "A file name is required."), Text(DocumentRules.MaxFileName)] string? FileName,
    [property: Required(ErrorMessage = "A content type is required.")] DocumentContentType? ContentType,
    [property: Required(ErrorMessage = "A size in bytes is required."), Range(1, DocumentRules.MaxByteSize, ErrorMessage = "A document must be at least one byte and 50 MB or smaller.")] long? ByteSize)
    : IValidatableObject
{
    /// <summary>The upload as the document rules take it, of the request's kind.</summary>
    /// <param name="kind">The kind of the request it answers.</param>
    /// <returns>The draft.</returns>
    public DocumentDraft Draft(DocumentKind kind) => new(kind, FileName!, ContentType!.Value, ByteSize!.Value);

    /// <summary>A file name, never a path.</summary>
    /// <param name="validationContext">Unused.</param>
    /// <returns>The failures.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (DocumentRules.FileNameProblem(FileName!) is { } problem)
        {
            yield return new ValidationResult(problem, [nameof(FileName)]);
        }
    }
}
