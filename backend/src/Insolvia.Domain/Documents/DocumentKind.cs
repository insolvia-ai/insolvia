using System.Text.Json.Serialization;

namespace Insolvia.Core.Documents;

/// <summary>
/// What a document is (Python's <c>documents.KINDS</c>). Here first because a
/// firm's document checklist files each entry under one; the rest of
/// <c>documents.py</c> is the case half's (18.7) and lands beside it.
/// </summary>
public enum DocumentKind
{
    /// <summary>A credit report.</summary>
    [JsonStringEnumMemberName("credit_report")]
    CreditReport,

    /// <summary>A pay stub.</summary>
    [JsonStringEnumMemberName("pay_stub")]
    PayStub,

    /// <summary>A bank statement.</summary>
    [JsonStringEnumMemberName("bank_statement")]
    BankStatement,

    /// <summary>A tax return.</summary>
    [JsonStringEnumMemberName("tax_return")]
    TaxReturn,

    /// <summary>Identification.</summary>
    [JsonStringEnumMemberName("identification")]
    Identification,

    /// <summary>A notice from the court.</summary>
    [JsonStringEnumMemberName("court_notice")]
    CourtNotice,

    /// <summary>Anything else.</summary>
    [JsonStringEnumMemberName("other")]
    Other,
}
