using System.Text.RegularExpressions;

namespace Insolvia.Aws.Sealing;

/// <summary>
/// The two KMS key aliases the deployed API seals under: the case key
/// (debtor tax ids) and the vault key (filing credentials). The API SEALS
/// under both and opens tax ids only — it never composes an
/// <see cref="Core.Ports.IFilingCredentialOpener"/>, and the vault key's policy
/// refuses it Decrypt in any case (ADR 0024, ADR 0028 § Data).
/// </summary>
/// <remarks>
/// <para>
/// <b>Where the values come from.</b> Only explicitly, from
/// <c>INSOLVIA_KMS_CASE_KEY_ALIAS</c> and <c>INSOLVIA_KMS_FILING_KEY_ALIAS</c>,
/// which every deployed host receives from its SSM namespace. Nothing derives
/// them: an unset variable is a refusal at boot.
/// </para>
/// <para>
/// <b>The values do not change</b>: <c>alias/insolvia-&lt;env&gt;-cases</c> and
/// <c>alias/insolvia-&lt;env&gt;-filing-credentials</c>, each one
/// <c>local.name</c> in its Terraform module, and the shape the deploy role's
/// <c>DenyCaseDataDecryption</c> fence matches. An explicit value of any other
/// shape is refused at boot rather than sealing under a key the policies do
/// not protect.
/// </para>
/// </remarks>
/// <param name="CaseKeyAlias">The tax-id key.</param>
/// <param name="FilingKeyAlias">The vault key.</param>
public sealed partial record SealingKeys(string CaseKeyAlias, string FilingKeyAlias)
{
    /// <summary>The explicit case-key variable.</summary>
    public const string CaseKeyVariable = "INSOLVIA_KMS_CASE_KEY_ALIAS";

    /// <summary>The explicit vault-key variable.</summary>
    public const string FilingKeyVariable = "INSOLVIA_KMS_FILING_KEY_ALIAS";

    /// <summary>
    /// Resolves both aliases from <paramref name="read"/>, or throws
    /// <see cref="InvalidOperationException"/> naming what is missing or malformed.
    /// </summary>
    /// <param name="read">Reads one variable; null when it is unset.</param>
    /// <returns>The two aliases.</returns>
    public static SealingKeys Load(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        string? Optional(string name) => read(name) is { Length: > 0 } value ? value : null;
        var caseKey = Optional(CaseKeyVariable);
        var filingKey = Optional(FilingKeyVariable);
        if (caseKey is null || filingKey is null)
        {
            throw new InvalidOperationException($"{CaseKeyVariable} and {FilingKeyVariable} must be set for the Lambda entrypoint");
        }

        if (!CaseKeyShape().IsMatch(caseKey))
        {
            throw new InvalidOperationException($"{CaseKeyVariable} must be alias/insolvia-<env>-cases, not '{caseKey}'");
        }

        if (!FilingKeyShape().IsMatch(filingKey))
        {
            throw new InvalidOperationException($"{FilingKeyVariable} must be alias/insolvia-<env>-filing-credentials, not '{filingKey}'");
        }

        return new SealingKeys(caseKey, filingKey);
    }

    [GeneratedRegex("^alias/insolvia-[a-z0-9]+(-[a-z0-9]+)*-cases$")]
    private static partial Regex CaseKeyShape();

    [GeneratedRegex("^alias/insolvia-[a-z0-9]+(-[a-z0-9]+)*-filing-credentials$")]
    private static partial Regex FilingKeyShape();
}
