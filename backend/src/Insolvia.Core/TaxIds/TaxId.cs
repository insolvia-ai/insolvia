using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Debtors;
using Insolvia.Core.Fields;
using Insolvia.Core.Ports;

namespace Insolvia.Core.TaxIds;

// The debtor's tax identifier (issue 13.12 / #382; from tax_ids.py) — the
// DOMAIN half: what is stored, the shape rules, the write and the logged
// read. The cipher (AES-256-GCM under a KMS-wrapped data key) is a port,
// ITaxIdCipher; its adapter is 18.12's. THE REF, NOT THE LOCATION, IS THE
// CONTRACT: a debtor names the sealed item by an opaque uuid, so a client's
// later matter can point at the same sealed number (ADR 0022).

/// <summary>
/// What a write carries after parsing. EXACTLY ONE of <see cref="Value"/>
/// (the nine digits, freshly typed) and <see cref="LastFour"/> (the client
/// echoing the view it was given: "keep what is stored") is set.
/// </summary>
public sealed record TaxIdInput(TaxIdKind Kind, string? Value = null, string? LastFour = null)
{
    /// <inheritdoc />
    public override string ToString() => $"TaxIdInput {{ Kind = {Kind}, LastFour = {LastFour ?? Value?[^4..]} }}";
}

/// <summary>What the debtor record carries — the kind, the last four and the reference to the sealed item. Never the number.</summary>
public sealed record TaxIdRef(TaxIdKind Kind, string LastFour, string Ref);

/// <summary>One sealed value: ciphertext, nonce and the wrapped data key — base64 text. Opaque to everything but the cipher adapters.</summary>
public sealed record Envelope(string Scheme, string Ciphertext, string Nonce, string WrappedKey);

/// <summary>The stored item: the envelope plus the facts the encryption context binds and the views need.</summary>
public sealed record SealedTaxId(string Ref, string FirmId, TaxIdKind Kind, string LastFour, Envelope Envelope, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>The tax-id rules and the two operations that touch a sealed value.</summary>
public static class TaxIdRules
{
    /// <summary>The one value the IAM grant conditions on (<c>kms:EncryptionContext:purpose</c>).</summary>
    public const string Purpose = "debtor-tax-id";

    /// <summary>Stamped into every envelope so a later scheme can coexist with this one.</summary>
    public const string SealedScheme = "kms-aes256-gcm/1";

    // The SSA's advertising block — never issued, accepted on purpose so a
    // committed fixture can carry an obviously synthetic SSN.
    private static readonly HashSet<string> SsaAdvertisingBlock =
        [.. Enumerable.Range(0, 10).Select(n => $"98765432{n}")];

    private static readonly (int Low, int High)[] ItinGroups = [(50, 65), (70, 88), (90, 92), (94, 99)];

    /// <summary>A fresh opaque reference — unguessable, so a ciphertext cannot be re-addressed.</summary>
    public static string NewRef() => Guid.NewGuid().ToString();

    /// <summary>
    /// The KMS encryption context AND the AES-GCM associated data: the firm and
    /// the ref — never the case, which is only where the item lives today.
    /// </summary>
    public static ValueDictionary<string, string> EncryptionContext(string firmId, string reference) =>
        new Dictionary<string, string>
        {
            ["purpose"] = Purpose,
            ["firm_id"] = firmId,
            ["tax_id_ref"] = reference,
        }.ToValueDictionary();

    /// <summary>
    /// Validate a <c>tax_id</c> member: <c>{kind, value}</c> for a number being
    /// entered, <c>{kind, last_four}</c> for one being kept. Absent is fine; every
    /// problem lands in <paramref name="errors"/> under <paramref name="path"/>.
    /// </summary>
    public static TaxIdInput? Parse(JsonNode? value, string path, IDictionary<string, string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (value is null)
        {
            return null;
        }

        var raw = FieldParsers.Mapping(value, path, errors);
        if (raw.Count == 0 && errors.ContainsKey(path))
        {
            return null;
        }

        if (!Wire.TryParse<TaxIdKind>(Json.AsString(raw["kind"]), out var kind))
        {
            errors[$"{path}.kind"] = "Must be one of " + string.Join(", ", Wire.Names<TaxIdKind>()) + ".";
            return null;
        }

        var full = raw["value"];
        var kept = raw["last_four"];
        if (full is not null && kept is not null)
        {
            errors[path] = "Send either the number or the last four, not both.";
            return null;
        }

        if (full is null && kept is null)
        {
            errors[path] = "A tax id needs its number (or the last four, to keep it).";
            return null;
        }

        if (kept is not null)
        {
            var keptText = Json.AsString(kept);
            if (keptText is null || keptText.Length != 4 || !PyText.IsDigits(keptText))
            {
                errors[$"{path}.last_four"] = "Must be exactly four digits.";
                return null;
            }

            return new TaxIdInput(kind, LastFour: keptText);
        }

        var fullText = Json.AsString(full);
        if (fullText is null)
        {
            errors[$"{path}.value"] = "Must be text.";
            return null;
        }

        var digits = Digits(fullText);
        if (digits is null)
        {
            errors[$"{path}.value"] = "Must be nine digits, with or without dashes.";
            return null;
        }

        var problem = kind == TaxIdKind.Ssn ? SsnProblem(digits) : ItinProblem(digits);
        if (problem is not null)
        {
            errors[$"{path}.value"] = problem;
            return null;
        }

        return new TaxIdInput(kind, Value: digits);
    }

    /// <summary>
    /// How B121 prints the number: an SSN as <c>000-00-0000</c>; an ITIN as the
    /// ten characters AFTER the pre-printed leading 9 (<c>00-00-0000</c>).
    /// </summary>
    public static string FormatForB121(TaxIdKind kind, string digits)
    {
        ArgumentNullException.ThrowIfNull(digits);
        return kind == TaxIdKind.Itin
            ? $"{digits[1..3]}-{digits[3..5]}-{digits[5..]}"
            : $"{digits[..3]}-{digits[3..5]}-{digits[5..]}";
    }

    /// <summary>
    /// Turn a parsed write into what the debtor will carry. Nothing given: no
    /// tax id (the sealed item is LEFT — it may be another matter's pointer). A
    /// number: sealed under the EXISTING ref when there is one, else a fresh
    /// ref. The last four alone: keep what is stored — refused, as a 400, when
    /// nothing is stored or the echo does not match.
    /// </summary>
    public static async Task<TaxIdRef?> StoreAsync(
        TaxIdInput? given,
        TaxIdRef? existing,
        string firmId,
        string caseId,
        ITaxIdCipher cipher,
        ITaxIdStore store,
        TimeProvider? clock = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cipher);
        ArgumentNullException.ThrowIfNull(store);
        if (given is null)
        {
            return null;
        }

        if (given.Value is null)
        {
            if (existing is null || existing.Kind != given.Kind || existing.LastFour != given.LastFour)
            {
                throw new FieldValidationException(new Dictionary<string, string>
                {
                    ["tax_id"] = "The stored tax id does not match — enter the full number to replace it.",
                });
            }

            return existing;
        }

        var reference = existing?.Ref ?? NewRef();
        var now = Clock.Now(clock);
        var previous = await store.GetAsync(caseId, reference, cancellationToken).ConfigureAwait(false);
        var envelope = await cipher.SealAsync(given.Value, EncryptionContext(firmId, reference), cancellationToken).ConfigureAwait(false);
        var lastFour = given.Value[^4..];
        await store.PutAsync(
            caseId,
            new SealedTaxId(reference, firmId, given.Kind, lastFour, envelope, previous?.CreatedAt ?? now, now),
            cancellationToken).ConfigureAwait(false);
        return new TaxIdRef(given.Kind, lastFour, reference);
    }

    /// <summary>
    /// THE FULL VALUE, and the one place it is produced. Records a
    /// <c>taxid.read</c> event FIRST (so a failed read still shows as
    /// attempted), then opens the envelope under the context it was sealed
    /// with. A ref the store does not hold answers null. Never call this from
    /// anything that answers a client.
    /// </summary>
    public static async Task<string?> ReadAsync(
        TaxIdRef taxId,
        string firmId,
        string caseId,
        FilingRole filingRole,
        string principal,
        string purpose,
        ITaxIdCipher cipher,
        ITaxIdStore store,
        IAccessLog accessLog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taxId);
        ArgumentNullException.ThrowIfNull(cipher);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(accessLog);
        await accessLog.RecordAsync(
            AccessEvents.Record(principal, AccessAction.TaxIdRead, caseId: caseId, filingRole: filingRole.ToWire(), purpose: purpose),
            cancellationToken).ConfigureAwait(false);
        var sealedId = await store.GetAsync(caseId, taxId.Ref, cancellationToken).ConfigureAwait(false);
        if (sealedId is null)
        {
            return null;
        }

        return await cipher.OpenAsync(sealedId.Envelope, EncryptionContext(firmId, sealedId.Ref), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Nine digits, or null. Dashes and spaces between the groups are tolerated, nothing else.</summary>
    private static string? Digits(string value)
    {
        var stripped = new string([.. value.Where(ch => ch is not '-' and not ' ')]);
        return stripped.Length == 9 && PyText.IsDigits(stripped) && stripped.All(char.IsAsciiDigit) ? stripped : null;
    }

    private static string? SsnProblem(string digits)
    {
        if (SsaAdvertisingBlock.Contains(digits))
        {
            return null;
        }

        var area = digits[..3];
        var group = digits[3..5];
        var serial = digits[5..];
        if (area is "000" or "666" || area.StartsWith('9'))
        {
            return "Not a Social Security number — no SSN begins with 000, 666 or 9.";
        }

        return group == "00" || serial == "0000"
            ? "Not a Social Security number — the middle or last group is all zeros."
            : null;
    }

    private static string? ItinProblem(string digits)
    {
        if (!digits.StartsWith('9'))
        {
            return "Not an ITIN — every ITIN begins with 9.";
        }

        var group = int.Parse(digits[3..5], System.Globalization.CultureInfo.InvariantCulture);
        return ItinGroups.Any(range => range.Low <= group && group <= range.High)
            ? null
            : "Not an ITIN — the middle two digits are outside the IRS's ranges.";
    }
}

/// <summary>Which kind of taxpayer number (Python's <c>TAX_ID_KINDS</c>). The first digit decides it.</summary>
public enum TaxIdKind
{
    /// <summary>A Social Security number.</summary>
    [JsonStringEnumMemberName("ssn")]
    Ssn,

    /// <summary>An individual taxpayer identification number (the 9xx block).</summary>
    [JsonStringEnumMemberName("itin")]
    Itin,
}
