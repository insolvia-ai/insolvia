namespace Insolvia.Api.Portal;

/// <summary>
/// A client's upload against one of the firm's document requests
/// (<c>POST /v1/portal/documents</c>). The document's kind is the request's.
/// Parsed by <c>DocumentRules.ParseUpload</c>, which owns validation.
/// </summary>
/// <param name="RequestId">The open document request this upload answers.</param>
/// <param name="FileName">The file's name.</param>
/// <param name="ContentType">The file's media type.</param>
/// <param name="ByteSize">The file's size in bytes.</param>
public sealed record PortalUploadRequest(string RequestId, string FileName, string ContentType, long ByteSize);
