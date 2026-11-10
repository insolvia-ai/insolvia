using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Insolvia.Api.Analysis;
using Insolvia.Domain.Engines;
using Insolvia.Domain.Forms;
using Insolvia.Domain.Jobs;

namespace Insolvia.Api.Cases;

/// <summary>
/// A pipeline job's status. Optional members are absent until set, so a
/// client tells "no result yet" from "empty result".
/// </summary>
/// <param name="Id">The job's id.</param>
/// <param name="Kind">What it does: <c>echo</c>, <c>packet_assembly</c>, <c>petition_review</c> or <c>document_extraction</c>.</param>
/// <param name="Status">Where it is.</param>
/// <param name="CreatedBy">Who accepted it.</param>
/// <param name="Attempts">How many attempts started.</param>
/// <param name="CreatedAt">Accepted, ISO 8601 UTC with milliseconds.</param>
/// <param name="UpdatedAt">Last changed, ISO 8601 UTC with milliseconds.</param>
/// <param name="DocumentId">The document a document-scoped kind works on.</param>
/// <param name="Options">The output options an options-scoped kind was accepted with, in full.</param>
/// <param name="Failure">Why it failed, when it did.</param>
/// <param name="Result">What it produced, when it succeeded; its shape is the kind's.</param>
public sealed record JobView(
    string Id,
    string Kind,
    JobStatus Status,
    string CreatedBy,
    int Attempts,
    string CreatedAt,
    string UpdatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DocumentId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OutputOptionsView? Options,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JobFailureView? Failure,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonObject? Result)
{
    /// <summary>The view of <paramref name="job"/>.</summary>
    /// <param name="job">The stored job.</param>
    /// <returns>The view.</returns>
    public static JobView Of(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new(
            job.Id,
            job.Kind,
            job.Status,
            job.CreatedBy,
            job.Attempts,
            JobRules.Timestamp(job.CreatedAt),
            JobRules.Timestamp(job.UpdatedAt),
            job.DocumentId,
            // Stored canonical (the accept route wrote OutputOptionsJson of a parse), so this reads it back exactly.
            job.Options is null ? null : OutputOptionsView.Of(FormOverlay.ParseOutputOptions(job.Options, allowForms: true)),
            job.Failure is null ? null : new JobFailureView(job.Failure.Category, job.Failure.Message),
            job.Result);
    }
}

/// <summary>Why a job failed.</summary>
/// <param name="Category">The failure's category, e.g. <c>enqueue_failed</c>.</param>
/// <param name="Message">What the preparer is told.</param>
public sealed record JobFailureView(string Category, string Message);

/// <summary>A job to accept (<c>POST …/jobs</c>). Parsed by <c>JobRules.ParseAcceptance</c>, which owns validation.</summary>
/// <param name="Kind"><c>echo</c>, <c>packet_assembly</c>, <c>petition_review</c> or <c>document_extraction</c>.</param>
/// <param name="DocumentId">The document, for <c>document_extraction</c> only (required there).</param>
/// <param name="Options">Output options, for <c>packet_assembly</c> only.</param>
public sealed record JobRequest(
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DocumentId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OutputOptionsRequest? Options);

/// <summary>An assembled filing packet.</summary>
/// <param name="Id">The packet's id.</param>
/// <param name="CaseId">The case.</param>
/// <param name="JobId">The assembly job that made it.</param>
/// <param name="FileName">The zip's file name.</param>
/// <param name="ContentType">The zip's media type.</param>
/// <param name="ByteSize">The zip's size.</param>
/// <param name="Sha256">The zip's SHA-256, hex.</param>
/// <param name="FormRevisions">The revision each form was printed from, by series.</param>
/// <param name="ConstantsSetId">The regulatory constants used.</param>
/// <param name="CreditorCount">Creditors on the matrix.</param>
/// <param name="CreatedBy">Who asked for it.</param>
/// <param name="CreatedAt">Assembled, ISO 8601 UTC with milliseconds.</param>
/// <param name="Options">The output options it was printed with.</param>
public sealed record PacketView(
    string Id,
    string CaseId,
    string JobId,
    string FileName,
    string ContentType,
    long ByteSize,
    string Sha256,
    IReadOnlyDictionary<string, string> FormRevisions,
    string ConstantsSetId,
    int CreditorCount,
    string CreatedBy,
    string CreatedAt,
    OutputOptionsView Options)
{
    /// <summary>The view of <paramref name="packet"/>.</summary>
    /// <param name="packet">The packet.</param>
    /// <returns>The view.</returns>
    public static PacketView Of(Packet packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        return new(packet.Id, packet.CaseId, packet.JobId, packet.FileName, packet.ContentType, packet.ByteSize, packet.Sha256, packet.FormRevisions, packet.ConstantsSetId, packet.CreditorCount, packet.CreatedBy, packet.CreatedAt, OutputOptionsView.Of(packet.Options));
    }
}

/// <summary>A case's packets, newest first.</summary>
/// <param name="Packets">The packets.</param>
public sealed record PacketList(IReadOnlyList<PacketView> Packets);

/// <summary>A short-lived URL serving a packet's bytes.</summary>
/// <param name="Url">The URL.</param>
/// <param name="Method">The HTTP method to fetch it with.</param>
/// <param name="ExpiresAt">When it stops working, ISO 8601 UTC with milliseconds.</param>
public sealed record PacketUrlView(string Url, string Method, string ExpiresAt);

/// <summary>What a case is worth, what it owes, and whether it could be filed.</summary>
/// <param name="ReadyToFile">Whether the completeness gate has no problems.</param>
/// <param name="Problems">The gate's problems — the same packet assembly runs.</param>
/// <param name="Totals">The schedule totals.</param>
/// <param name="Liens">The derived collateral figures.</param>
public sealed record CaseSummaryView(bool ReadyToFile, IReadOnlyList<PacketProblemView> Problems, CaseTotalsView Totals, LienView Liens)
{
    /// <summary>The view of <paramref name="summary"/>.</summary>
    /// <param name="summary">The summary.</param>
    /// <returns>The view.</returns>
    public static CaseSummaryView Of(CaseSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var totals = summary.Totals;
        return new(
            summary.ReadyToFile,
            [.. summary.Problems.Select(PacketProblemView.Of)],
            new(
                Exact.F(totals.RealEstate),
                Exact.F(totals.PersonalProperty),
                Exact.F(totals.Assets),
                Exact.F(totals.TotalExempt),
                Exact.F(totals.TotalNonExempt),
                Exact.F(totals.Secured),
                Exact.F(totals.PriorityUnsecured),
                Exact.F(totals.NonpriorityUnsecured),
                Exact.F(totals.Liabilities),
                Exact.F(totals.MonthlyIncome),
                Exact.F(totals.MonthlyExpenses),
                Exact.F(totals.MonthlyExcess)),
            LienView.Of(summary.Liens));
    }
}

/// <summary>The schedule totals, as decimal strings.</summary>
/// <param name="RealEstate">Schedule A/B real estate.</param>
/// <param name="PersonalProperty">Schedule A/B personal property.</param>
/// <param name="Assets">Both, summed.</param>
/// <param name="TotalExempt">The exemption claims' amounts, summed.</param>
/// <param name="TotalNonExempt">Assets less that.</param>
/// <param name="Secured">Schedule D.</param>
/// <param name="PriorityUnsecured">Schedule E/F priority.</param>
/// <param name="NonpriorityUnsecured">Schedule E/F nonpriority.</param>
/// <param name="Liabilities">All three, summed.</param>
/// <param name="MonthlyIncome">Schedule I.</param>
/// <param name="MonthlyExpenses">Schedule J.</param>
/// <param name="MonthlyExcess">Income less expenses.</param>
public sealed record CaseTotalsView(
    string RealEstate,
    string PersonalProperty,
    string Assets,
    string TotalExempt,
    string TotalNonExempt,
    string Secured,
    string PriorityUnsecured,
    string NonpriorityUnsecured,
    string Liabilities,
    string MonthlyIncome,
    string MonthlyExpenses,
    string MonthlyExcess);
