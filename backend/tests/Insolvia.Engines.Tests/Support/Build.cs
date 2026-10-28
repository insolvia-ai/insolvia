using System.Text.Json.Nodes;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Cases;
using Insolvia.Core.Debtors;
using Insolvia.Core.Fields;

namespace Insolvia.Engines.Tests.Support;

/// <summary>The record builders the translated suites share — every identifier obviously fake (this repo is public).</summary>
internal static class Build
{
    public static readonly DateTimeOffset Created = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    public static Debtor Debtor(FilingRole role, string id, Address? residence = null, string caseId = "case-0001") =>
        new(id, caseId, role, Created, Created, DebtorBody.Empty with { ResidenceAddress = residence ?? new Address() });

    public static Debtor Debtor(FilingRole role, string id, string state, string? county = null) =>
        Debtor(role, id, new Address(State: state, County: county));

    public static Case Case(int chapter = 7, string district = "Middle District of Florida", DateTimeOffset? createdAt = null) =>
        new("case-0001", "firm-0001", "subject-0001", chapter, district, CaseStatus.Intake, createdAt ?? Created, createdAt ?? Created);

    /// <summary>A body from a JSON literal, through its kind's parser.</summary>
    public static TBody Body<TBody>(EntityKind<TBody> kind, string json)
        where TBody : class => CaseFiles.Body(kind, json);

    public static JsonObject Json(string json) => JsonNode.Parse(json)!.AsObject();
}
