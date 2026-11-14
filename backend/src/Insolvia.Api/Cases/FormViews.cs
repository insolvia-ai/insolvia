using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Insolvia.Domain;
using Insolvia.Domain.Forms;

namespace Insolvia.Api.Cases;

/// <summary>One reason a form or the packet cannot be produced yet — a completeness-gate problem.</summary>
/// <param name="Source">The form series or rule that raised it.</param>
/// <param name="Message">What is wrong, for a person.</param>
/// <param name="ItemId">The record at fault; absent for a case-wide problem.</param>
/// <param name="Field">The field at fault; absent when the problem is not one field's.</param>
public sealed record PacketProblemView(
    string Source,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ItemId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Field)
{
    /// <summary>The view of <paramref name="problem"/>.</summary>
    /// <param name="problem">The gate's problem.</param>
    /// <returns>The view.</returns>
    public static PacketProblemView Of(PacketProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        return new(problem.Source, problem.Message, problem.ItemId, problem.Field.Length > 0 ? problem.Field : null);
    }
}

/// <summary>How forms are printed: the full set of options, always.</summary>
/// <param name="DraftWatermark">A "DRAFT" watermark on every page.</param>
/// <param name="PrintDate">A date and time stamp in the top margin.</param>
/// <param name="SignaturePages">Which pages: <c>all</c>, <c>only</c> the signature pages, or <c>omit</c> them.</param>
/// <param name="SignElectronically"><c>/s/ Name</c> on every debtor signature line.</param>
/// <param name="AmendedOnly">Only the forms and records marked amended.</param>
/// <param name="Forms">Only these forms, by short key; absent for every form.</param>
public sealed record OutputOptionsView(
    bool DraftWatermark,
    bool PrintDate,
    string SignaturePages,
    bool SignElectronically,
    bool AmendedOnly,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Forms)
{
    /// <summary>The view of <paramref name="options"/>.</summary>
    /// <param name="options">The options.</param>
    /// <returns>The view.</returns>
    public static OutputOptionsView Of(OutputOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new(options.DraftWatermark, options.PrintDate, FormOverlay.Wire(options.SignaturePages), options.SignElectronically, options.AmendedOnly, options.Forms);
    }
}

/// <summary>Output options as sent; every one optional, an unknown one a 400.</summary>
/// <param name="DraftWatermark">A "DRAFT" watermark on every page.</param>
/// <param name="PrintDate">A date and time stamp in the top margin.</param>
/// <param name="SignaturePages"><c>all</c>, <c>only</c> or <c>omit</c>.</param>
/// <param name="SignElectronically"><c>/s/ Name</c> on every debtor signature line.</param>
/// <param name="AmendedOnly">Only the forms and records marked amended.</param>
/// <param name="Forms">Only these forms, by short key (<c>b101</c>).</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed partial record OutputOptionsRequest(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? DraftWatermark = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? PrintDate = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), AllowedValues(null, "all", "only", "omit", ErrorMessage = "must be one of all, only, omit.")] string? SignaturePages = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? SignElectronically = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? AmendedOnly = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Forms = null)
    : IValidatableObject
{
    /// <summary>The options, unsent ones at their defaults.</summary>
    /// <returns>The options.</returns>
    public OutputOptions ToOptions() => new()
    {
        DraftWatermark = DraftWatermark ?? false,
        PrintDate = PrintDate ?? false,
        SignaturePages = SignaturePages switch { "only" => SignaturePagesMode.Only, "omit" => SignaturePagesMode.Omit, _ => SignaturePagesMode.All },
        SignElectronically = SignElectronically ?? false,
        AmendedOnly = AmendedOnly ?? false,
        Forms = Forms?.ToValueCollection(),
    };

    /// <summary>A forms subset is distinct short keys, and never beside <c>amendedOnly</c>.</summary>
    /// <param name="validationContext">Unused.</param>
    /// <returns>The failures.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Forms is null)
        {
            yield break;
        }

        if (Forms.Count == 0 || Forms.Any(form => form is null || !FormKey().IsMatch(form)))
        {
            yield return new ValidationResult("must be a non-empty list of lower-case form keys (e.g. \"b101\").", [nameof(Forms)]);
        }
        else if (Forms.Distinct(StringComparer.Ordinal).Count() != Forms.Count)
        {
            yield return new ValidationResult("names the same form more than once.", [nameof(Forms)]);
        }
        else if (AmendedOnly == true)
        {
            yield return new ValidationResult("cannot be combined with an explicit forms subset.", [nameof(AmendedOnly)]);
        }
    }

    [GeneratedRegex("^[a-z0-9]+$")]
    private static partial Regex FormKey();
}

/// <summary>One form the case files, with its status.</summary>
/// <param name="Series">The form series, e.g. <c>form/b101</c>.</param>
/// <param name="Form">The short key, e.g. <c>b101</c> — what the preview URL takes.</param>
/// <param name="Title">The form's title.</param>
/// <param name="OfficialNumber">The official form number, e.g. <c>101</c>.</param>
/// <param name="Problems">Why it cannot render yet; empty when it can.</param>
/// <param name="OpenTaskCount">Open tasks anchored to the form; present at zero.</param>
/// <param name="Metric">The form's headline figure; absent where the form defines none.</param>
public sealed record FormSummaryView(
    string Series,
    string Form,
    string Title,
    string OfficialNumber,
    IReadOnlyList<PacketProblemView> Problems,
    int OpenTaskCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FormMetricView? Metric)
{
    /// <summary>The view of <paramref name="summary"/>.</summary>
    /// <param name="summary">The hub row.</param>
    /// <param name="openTaskCount">Open tasks anchored to the form.</param>
    /// <returns>The view.</returns>
    public static FormSummaryView Of(FormSummary summary, int openTaskCount)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return new(
            summary.Series,
            summary.Form,
            summary.Title,
            summary.OfficialNumber,
            [.. summary.Problems.Select(PacketProblemView.Of)],
            openTaskCount,
            summary.Metric is { } metric ? new FormMetricView(metric.Kind, metric.Value) : null);
    }
}

/// <summary>A form's headline figure.</summary>
/// <param name="Kind">What it measures.</param>
/// <param name="Value">The figure.</param>
public sealed record FormMetricView(string Kind, string Value);

/// <summary>Every form the case files.</summary>
/// <param name="Forms">The forms, in filing order.</param>
public sealed record FormList(IReadOnlyList<FormSummaryView> Forms);

/// <summary>
/// One form's preview: a short-lived URL to the rendered PDF with empty
/// <c>problems</c>, or every reason it cannot render and no URL — never both.
/// </summary>
/// <param name="Problems">Why it cannot render; empty when it rendered.</param>
/// <param name="Options">The output options it was rendered with.</param>
/// <param name="Url">The PDF's URL; absent when it did not render.</param>
/// <param name="Method">The HTTP method to fetch it with; absent with the URL.</param>
/// <param name="ExpiresAt">When the URL stops working, ISO 8601 UTC; absent with the URL.</param>
public sealed record FormPreviewView(
    IReadOnlyList<PacketProblemView> Problems,
    OutputOptionsView Options,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Url,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Method,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExpiresAt);

/// <summary>The filing set and hand-off checklist for the case's district. Optional members are absent, never null.</summary>
/// <param name="RegistryRelease">The court registry release it was built from.</param>
/// <param name="FilingMethod">How the district takes filings.</param>
/// <param name="OrderBasis">Where the document order comes from.</param>
/// <param name="NamesBasis">Where the file names come from.</param>
/// <param name="MaxBytes">The largest file the district accepts.</param>
/// <param name="MaxBytesBasis">Where that limit comes from.</param>
/// <param name="Documents">The documents to file, in order.</param>
/// <param name="Checklist">What to do before and after filing.</param>
/// <param name="Court">The court; absent when the case names none the registry knows.</param>
/// <param name="Packet">The packet the documents were measured from; absent when none is assembled.</param>
public sealed record FilingSetView(
    string RegistryRelease,
    string FilingMethod,
    string OrderBasis,
    string NamesBasis,
    long MaxBytes,
    string MaxBytesBasis,
    IReadOnlyList<FilingDocumentView> Documents,
    IReadOnlyList<FilingChecklistItemView> Checklist,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FilingCourtView? Court,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FilingSetPacketView? Packet)
{
    /// <summary>The view of <paramref name="filingSet"/>.</summary>
    /// <param name="filingSet">The filing set.</param>
    /// <returns>The view.</returns>
    public static FilingSetView Of(FilingSet filingSet)
    {
        ArgumentNullException.ThrowIfNull(filingSet);
        return new(
            filingSet.RegistryRelease,
            filingSet.FilingMethod,
            filingSet.OrderBasis,
            filingSet.NamesBasis,
            filingSet.MaxBytes,
            filingSet.MaxBytesBasis,
            [.. filingSet.Documents.Select(FilingDocumentView.Of)],
            [.. filingSet.Checklist.Select(item => new FilingChecklistItemView(item.Id, item.Status, item.Title, item.Detail, item.Link))],
            filingSet.CourtCode is { } code && filingSet.CourtName is { } name ? new FilingCourtView(code, name, filingSet.DivisionName) : null,
            filingSet.Packet is { } packet ? new FilingSetPacketView(packet.Id, packet.CreatedAt) : null);
    }
}

/// <summary>The court a case files in.</summary>
/// <param name="Code">The court's code.</param>
/// <param name="Name">The court's name.</param>
/// <param name="DivisionName">The division; absent when the case names none.</param>
public sealed record FilingCourtView(
    string Code,
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DivisionName);

/// <summary>The packet a filing set was measured from.</summary>
/// <param name="Id">The packet.</param>
/// <param name="CreatedAt">When it was assembled, ISO 8601 UTC.</param>
public sealed record FilingSetPacketView(string Id, string CreatedAt);

/// <summary>One document of the filing set.</summary>
/// <param name="Key">The document's key.</param>
/// <param name="Title">The document, for a person.</param>
/// <param name="FileName">The name to file it under.</param>
/// <param name="Source">Where it comes from.</param>
/// <param name="Handling">How it is filed.</param>
/// <param name="Checks">The district's checks against the packet's file.</param>
/// <param name="Note">A note for the filer; absent when none.</param>
public sealed record FilingDocumentView(
    string Key,
    string Title,
    string FileName,
    string Source,
    string Handling,
    IReadOnlyList<FilingCheckView> Checks,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Note)
{
    /// <summary>The view of <paramref name="document"/>.</summary>
    /// <param name="document">The document.</param>
    /// <returns>The view.</returns>
    public static FilingDocumentView Of(FilingDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new(
            document.Key,
            document.Title,
            document.FileName,
            document.Source,
            document.Handling,
            [.. document.Checks.Select(check => new FilingCheckView(check.Check, check.Outcome, check.Message))],
            document.Note.Length > 0 ? document.Note : null);
    }
}

/// <summary>One district check of a document.</summary>
/// <param name="Check">What was checked.</param>
/// <param name="Outcome">The result.</param>
/// <param name="Message">The result, for a person.</param>
public sealed record FilingCheckView(string Check, string Outcome, string Message);

/// <summary>One step of the hand-off checklist.</summary>
/// <param name="Id">The step's id.</param>
/// <param name="Status">Where it stands.</param>
/// <param name="Title">The step.</param>
/// <param name="Detail">What it involves.</param>
/// <param name="Link">Where to do it; absent when nowhere in particular.</param>
public sealed record FilingChecklistItemView(
    string Id,
    string Status,
    string Title,
    string Detail,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Link);
