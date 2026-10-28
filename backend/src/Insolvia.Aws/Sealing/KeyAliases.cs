namespace Insolvia.Aws.Sealing;

/// <summary>
/// The two KMS key aliases, DERIVED from the case table's name exactly as the
/// Python composers derive them (<c>case_key_alias</c>,
/// <c>filing_credentials_key_alias</c>) from <c>CASE_TABLE_NAME</c>. Each is one
/// <c>local.name</c> in its Terraform module, which is what makes this a
/// derivation rather than a coincidence, and the deploy role's
/// <c>DenyCaseDataDecryption</c> fence matches the same pattern — so the
/// ciphers need no configuration of their own in any environment.
/// </summary>
public static class KeyAliases
{
    private const string CaseTableSuffix = "-cases";
    private const string VaultSuffix = "-filing-credentials";

    /// <summary><c>insolvia-&lt;env&gt;-cases</c> → <c>alias/insolvia-&lt;env&gt;-cases</c>: the tax-id key.</summary>
    public static string CaseKey(string caseTableName)
    {
        ArgumentException.ThrowIfNullOrEmpty(caseTableName);
        return $"alias/{caseTableName}";
    }

    /// <summary>
    /// <c>insolvia-&lt;env&gt;-cases</c> → <c>insolvia-&lt;env&gt;-filing-credentials</c>.
    /// Refuses a name that is not a case table's rather than guessing: a wrong
    /// guess is a vault sealed under a key the policy does not protect.
    /// </summary>
    public static string FilingCredentialsName(string caseTableName)
    {
        ArgumentNullException.ThrowIfNull(caseTableName);
        if (!caseTableName.EndsWith(CaseTableSuffix, StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{caseTableName}' is not an insolvia-<env>-cases table name", nameof(caseTableName));
        }

        return caseTableName[..^CaseTableSuffix.Length] + VaultSuffix;
    }

    /// <summary>The vault key's alias — the vault's name under <c>alias/</c>.</summary>
    public static string FilingCredentialsKey(string caseTableName) => $"alias/{FilingCredentialsName(caseTableName)}";
}
