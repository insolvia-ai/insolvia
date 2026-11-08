using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Insolvia.Core.Ports;

namespace Insolvia.Core.Courts;

/// <summary>
/// The court registry: the committed <c>courts/us-bankruptcy</c> releases,
/// shipped inside this assembly as embedded resources (ADR 0014 — the
/// registry is read by the CASE DOMAIN, so it ships with the domain), and the
/// resolver a case's court reference is validated against (from
/// <c>courts.py</c>). Codes match exactly, as the contract requires.
/// </summary>
public sealed class CourtRegistry : ICourtRegistry
{
    /// <summary>The series id.</summary>
    public const string SeriesId = "courts/us-bankruptcy";

    private const string ResourcePrefix = "Insolvia.Core.Regulatory/";

    private static readonly Lazy<CourtRegistry> ShippedRegistry = new(() => new CourtRegistry(CourtRegistryLoader.Load(EmbeddedFiles())));

    private readonly TimeProvider _clock;

    /// <summary>Creates a registry over already-loaded releases.</summary>
    public CourtRegistry(IReadOnlyList<CourtsRelease> releases, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(releases);
        Releases = releases;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The committed registry, shipped inside this assembly — loaded once, validated on load.</summary>
    public static CourtRegistry Shipped => ShippedRegistry.Value;

    /// <summary>Every release, oldest first.</summary>
    public IReadOnlyList<CourtsRelease> Releases { get; }

    /// <summary>Newest by (effective date, sequence), even if still in the future.</summary>
    public CourtsRelease Latest => Releases[^1];

    /// <summary>
    /// The release in force today — what a floating case resolves against. A
    /// court's identity does not move the way a dollar figure does, so every
    /// "does this court exist" question reads this.
    /// </summary>
    public CourtsRelease Current => Resolve(Clock.Today(_clock));

    /// <summary>
    /// The release with the greatest effective date on or before
    /// <paramref name="asOf"/>, ties broken by highest sequence. No fallback
    /// past the beginning: <see cref="KeyNotFoundException"/>.
    /// </summary>
    public CourtsRelease Resolve(DateOnly asOf) => PickRelease(Releases, asOf);

    /// <summary>That exact release — must succeed forever for any id ever pinned.</summary>
    public CourtsRelease Get(string releaseId) =>
        Releases.FirstOrDefault(release => release.ReleaseId == releaseId)
        ?? throw new KeyNotFoundException($"{SeriesId} has no release '{releaseId}'");

    /// <summary>The current release's district, or null.</summary>
    public CourtDistrict? District(string code) => Current.District(code);

    /// <summary>The court and division a case reference names, or null when the registry does not know the pair.</summary>
    public (CourtDistrict Court, Division Division)? Division(string courtCode, string divisionCode)
    {
        var court = District(courtCode);
        var found = court?.Division(divisionCode);
        return court is null || found is null ? null : (court, found);
    }

    /// <summary>Every current district's code.</summary>
    public IReadOnlyList<string> DistrictCodes() => [.. Current.Districts.Select(district => district.Code)];

    /// <inheritdoc />
    public bool HasDistrict(string courtCode) => District(courtCode) is not null;

    /// <inheritdoc />
    public bool HasDivision(string courtCode, string divisionCode) => Division(courtCode, divisionCode) is not null;

    /// <summary>The release in force on <paramref name="asOf"/> among <paramref name="candidates"/>.</summary>
    public static CourtsRelease PickRelease(IReadOnlyList<CourtsRelease> candidates, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var applicable = candidates.Where(release => release.EffectiveDate <= asOf).ToList();
        if (applicable.Count == 0)
        {
            var earliest = candidates.Min(release => release.EffectiveDate);
            throw new KeyNotFoundException(
                $"no release of {SeriesId} is effective on or before {Iso(asOf)} (series begins {Iso(earliest)})");
        }

        return applicable.OrderBy(release => release.EffectiveDate).ThenBy(release => release.Sequence).Last();
    }

    /// <summary>
    /// The registry's files as embedded, keyed by their path under the
    /// registry root (<c>courts/us-bankruptcy/&lt;release&gt;/…</c>).
    /// </summary>
    internal static Dictionary<string, string> EmbeddedFiles()
    {
        var assembly = typeof(CourtRegistry).Assembly;
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix + "courts/", StringComparison.Ordinal))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            files[name[ResourcePrefix.Length..]] = reader.ReadToEnd();
        }

        return files;
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>
/// Loads and validates every release of the series (Python's
/// <c>load_registry</c>). A malformed release fails the load with an
/// <see cref="InvalidDataException"/> — run over the committed registry by the
/// unit tests, so a bad record fails a pull request rather than a filing.
/// </summary>
public static partial class CourtRegistryLoader
{
    private static readonly IReadOnlyList<string> StatesWithFips = ["FL", "GA", "TX"];

    private static readonly Dictionary<string, string> StateFips = new(StringComparer.Ordinal)
    {
        ["FL"] = "12",
        ["GA"] = "13",
        ["TX"] = "48",
    };

    /// <summary>
    /// Every release under <paramref name="files"/> — paths relative to the
    /// registry root (<c>courts/us-bankruptcy/&lt;release&gt;/manifest.json</c>,
    /// <c>…/districts/&lt;code&gt;.json</c>) mapped to their text.
    /// </summary>
    public static IReadOnlyList<CourtsRelease> Load(IReadOnlyDictionary<string, string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        const string series = "courts/us-bankruptcy/";
        var releaseDirs = files.Keys
            .Where(path => path.StartsWith(series, StringComparison.Ordinal))
            .Select(path => path[series.Length..])
            .Where(rest => rest.Contains('/', StringComparison.Ordinal))
            .Select(rest => rest[..rest.IndexOf('/', StringComparison.Ordinal)])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        var loaded = releaseDirs.Select(dir => LoadRelease(files, series + dir, dir)).ToList();
        if (loaded.Count == 0)
        {
            throw new InvalidDataException($"the {CourtRegistry.SeriesId} series has no releases");
        }

        loaded = [.. loaded.OrderBy(release => release.EffectiveDate).ThenBy(release => release.Sequence)];
        if (loaded.Select(release => release.ReleaseId).Distinct(StringComparer.Ordinal).Count() != loaded.Count)
        {
            throw new InvalidDataException($"the {CourtRegistry.SeriesId} series has duplicate release ids");
        }

        return loaded;
    }

    private static InvalidDataException Fail(string where, string problem) => new($"malformed court record {where}: {problem}");

    private static string Repr(JsonNode? value) => value is null ? "None" : value.ToJsonString();

    private static string StrField(JsonObject data, string key, string where)
    {
        var value = Json.AsString(data[key]);
        if (value is null || TextRules.Strip(value).Length == 0)
        {
            throw Fail(where, $"{key} missing or empty");
        }

        return value;
    }

    private static string? OptionalStr(JsonObject data, string key, string where)
    {
        var value = data[key];
        if (value is null)
        {
            return null;
        }

        return Json.AsString(value) ?? throw Fail(where, $"{key} must be a string or null");
    }

    private static string Notes(JsonObject data, string key, string where)
    {
        var value = data[key];
        if (value is null)
        {
            return string.Empty;
        }

        return Json.AsString(value) ?? throw Fail(where, $"{key} must be a string");
    }

    private static DateOnly? Date(JsonNode? value, string where, string fieldName)
    {
        if (value is null)
        {
            return null;
        }

        var text = Json.AsString(value) ?? throw Fail(where, $"{fieldName} {Repr(value)} is not a date string");
        if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            throw Fail(where, $"{fieldName} '{text}': Invalid isoformat string");
        }

        return parsed;
    }

    private static ValueCollection<Source> Sources(JsonNode? raw, string where)
    {
        if (raw is not JsonArray list || list.Count == 0)
        {
            throw Fail(where, "sources missing or empty");
        }

        var parsed = new List<Source>();
        foreach (var entry in list)
        {
            if (entry is not JsonObject obj)
            {
                throw Fail(where, "source is not an object");
            }

            var read = Date(obj["read"], where, "source.read") ?? throw Fail(where, "source.read missing");
            parsed.Add(new Source(StrField(obj, "id", where), StrField(obj, "title", where), StrField(obj, "url", where), read));
        }

        if (parsed.Select(source => source.Id).Distinct(StringComparer.Ordinal).Count() != parsed.Count)
        {
            throw Fail(where, "duplicate source ids");
        }

        return parsed.ToValueCollection();
    }

    private static Courthouse ParseCourthouse(JsonObject raw, string where) => new(
        StrField(raw, "name", where),
        StrField(raw, "line1", where),
        OptionalStr(raw, "line2", where),
        StrField(raw, "city", where),
        StrField(raw, "state", where),
        StrField(raw, "postal_code", where));

    private static Division ParseDivision(JsonNode? node, string where, Facts facts, string state)
    {
        if (node is not JsonObject raw)
        {
            throw Fail(where, "division is not an object");
        }

        var code = StrField(raw, "code", where);
        where = $"{where} division {code}";
        if (!DivisionCode().IsMatch(code))
        {
            throw Fail(where, "code must be a lowercase slug");
        }

        var divisionFacts = new Facts(facts.SourceIds, where);
        var (courthouseRaw, courthouseFact) = divisionFacts.Mapping(raw, "courthouse");
        var courthouse = new Fact<Courthouse>(
            courthouseRaw is null ? null : ParseCourthouse(courthouseRaw, $"{where} courthouse"),
            courthouseFact.Status,
            courthouseFact.Source,
            courthouseFact.VerifiedAt,
            courthouseFact.Note);
        if (raw["counties"] is not JsonArray countiesRaw || countiesRaw.Count == 0)
        {
            throw Fail(where, "counties missing or empty");
        }

        var counties = new List<County>();
        foreach (var entry in countiesRaw)
        {
            if (entry is not JsonObject county)
            {
                throw Fail(where, "county is not an object");
            }

            var fips = StrField(county, "fips", where);
            if (!Fips().IsMatch(fips) || !fips.StartsWith(StateFips[state], StringComparison.Ordinal))
            {
                throw Fail(where, $"county fips '{fips}' is not a {state} FIPS-5 code");
            }

            counties.Add(new County(StrField(county, "name", where), fips));
        }

        var countiesSource = StrField(raw, "counties_source", where);
        if (!facts.SourceIds.Contains(countiesSource))
        {
            throw Fail(where, $"counties_source '{countiesSource}' is not a source");
        }

        return new Division(code, StrField(raw, "name", where), divisionFacts.String(raw, "office_code"), courthouse, counties.ToValueCollection(), countiesSource);
    }

    private static ValueCollection<LocalForm> LocalForms(JsonNode? raw, string where, IReadOnlySet<string> sourceIds)
    {
        if (raw is null)
        {
            return [];
        }

        if (raw is not JsonArray list)
        {
            throw Fail(where, "local_forms must be a list");
        }

        var forms = new List<LocalForm>();
        foreach (var entry in list)
        {
            if (entry is not JsonObject obj)
            {
                throw Fail(where, "local form is not an object");
            }

            var source = StrField(obj, "source", where);
            if (!sourceIds.Contains(source))
            {
                throw Fail(where, $"local form source '{source}' is not a source");
            }

            forms.Add(new LocalForm(StrField(obj, "id", where), StrField(obj, "title", where), OptionalStr(obj, "url", where), StrField(obj, "when_required", where), source));
        }

        return forms.ToValueCollection();
    }

    private static string Predates(string added) => $"not recorded in this release — the fact was added on {added}";

    private static Fact<ValueCollection<string>> DocketOrder(JsonObject raw, string where, Facts facts)
    {
        if (!raw.ContainsKey("docket_order"))
        {
            return new Fact<ValueCollection<string>>(null, FactStatus.Unverified, null, null, Predates("2026-10-06"));
        }

        var (value, fact) = facts.Raw(raw, "docket_order");
        ValueCollection<string>? order = null;
        if (value is not null)
        {
            var dw = $"{where} docket_order";
            if (value is not JsonArray list || list.Count == 0)
            {
                throw Fail(dw, "value must be a non-empty list of document keys");
            }

            var keys = new List<string>();
            foreach (var key in list)
            {
                var text = Json.AsString(key);
                if (text is null || !DocumentKey().IsMatch(text))
                {
                    throw Fail(dw, $"{QuotedRepr(key)} is not a document key");
                }

                keys.Add(text);
            }

            if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Count)
            {
                throw Fail(dw, "a document key appears twice");
            }

            order = keys.ToValueCollection();
        }

        return new Fact<ValueCollection<string>>(order, fact.Status, fact.Source, fact.VerifiedAt, fact.Note);
    }

    private static Fact<ValueDictionary<string, string>> FileNames(JsonObject raw, string where, Facts facts)
    {
        if (!raw.ContainsKey("file_names"))
        {
            return new Fact<ValueDictionary<string, string>>(null, FactStatus.Unverified, null, null, Predates("2026-10-06"));
        }

        var (value, fact) = facts.Mapping(raw, "file_names");
        ValueDictionary<string, string>? names = null;
        if (value is not null)
        {
            var nw = $"{where} file_names";
            var pairs = new List<KeyValuePair<string, string>>();
            foreach (var (key, name) in value)
            {
                if (!DocumentKey().IsMatch(key))
                {
                    throw Fail(nw, $"'{key}' is not a document key");
                }

                var text = Json.AsString(name);
                if (text is null || !FileName().IsMatch(text))
                {
                    throw Fail(nw, $"{QuotedRepr(name)} is not a plain .pdf/.txt file name");
                }

                pairs.Add(new(key, text));
            }

            if (pairs.Select(pair => pair.Value).Distinct(StringComparer.Ordinal).Count() != pairs.Count)
            {
                throw Fail(nw, "two documents share a file name");
            }

            names = pairs.ToValueDictionary();
        }

        return new Fact<ValueDictionary<string, string>>(names, fact.Status, fact.Source, fact.VerifiedAt, fact.Note);
    }

    private static Opening ParseOpening(JsonNode? node, string where, Facts facts)
    {
        if (node is not JsonObject raw)
        {
            throw Fail(where, "opening is not an object");
        }

        where = $"{where} opening";
        var openingFacts = new Facts(facts.SourceIds, where);
        var (instrumentRaw, instrumentFact) = openingFacts.Mapping(raw, "signature_instrument");
        SignatureInstrument? instrument = null;
        if (instrumentRaw is not null)
        {
            var iw = $"{where} signature_instrument";
            var wetInk = instrumentRaw["wet_ink_required"];
            var ownEvent = instrumentRaw["own_docket_event"];
            if (wetInk is not null && Json.AsBool(wetInk) is null)
            {
                throw Fail(iw, "wet_ink_required must be a boolean or null");
            }

            if (ownEvent is not null && Json.AsBool(ownEvent) is null)
            {
                throw Fail(iw, "own_docket_event must be a boolean or null");
            }

            instrument = new SignatureInstrument(
                StrField(instrumentRaw, "kind", iw),
                StrField(instrumentRaw, "title", iw),
                Json.AsBool(wetInk),
                OptionalStr(instrumentRaw, "retention", iw),
                Json.AsBool(ownEvent),
                OptionalStr(instrumentRaw, "form_url", iw));
        }

        var (feeRaw, feeFact) = openingFacts.Mapping(raw, "fee_rule");
        var fee = feeRaw is null
            ? null
            : new FeeRule(StrField(feeRaw, "deadline", $"{where} fee_rule"), Notes(feeRaw, "note", $"{where} fee_rule"));
        return new Opening(
            new Fact<SignatureInstrument>(instrument, instrumentFact.Status, instrumentFact.Source, instrumentFact.VerifiedAt, instrumentFact.Note),
            openingFacts.Choice<SsnStatementHandling>(raw, "ssn_statement"),
            LocalForms(raw["local_forms"], where, facts.SourceIds),
            new Fact<FeeRule>(fee, feeFact.Status, feeFact.Source, feeFact.VerifiedAt, feeFact.Note),
            DocketOrder(raw, where, openingFacts),
            FileNames(raw, where, openingFacts));
    }

    private static Fact<Chapter13PlanForm> Chapter13Plan(JsonObject raw, string where, Facts facts)
    {
        if (!raw.ContainsKey("chapter_13_plan"))
        {
            return new Fact<Chapter13PlanForm>(null, FactStatus.Unverified, null, null, Predates("2026-09-26"));
        }

        var (value, fact) = facts.Mapping(raw, "chapter_13_plan");
        Chapter13PlanForm? plan = null;
        if (value is not null)
        {
            var pw = $"{where} chapter_13_plan";
            if (!Wire.TryParse<Chapter13PlanFormKind>(Json.AsString(value["form"]), out var form))
            {
                throw Fail(pw, "form must be one of ('official', 'local')");
            }

            var title = OptionalStr(value, "title", pw);
            if (form == Chapter13PlanFormKind.Local && string.IsNullOrEmpty(title))
            {
                throw Fail(pw, "a local plan form needs a title — name the form");
            }

            plan = new Chapter13PlanForm(form, title, OptionalStr(value, "url", pw));
        }

        return new Fact<Chapter13PlanForm>(plan, fact.Status, fact.Source, fact.VerifiedAt, fact.Note);
    }

    private static CaseUpload ParseCaseUpload(JsonNode? node, string where)
    {
        if (node is not JsonObject raw)
        {
            throw Fail(where, "case_upload is not an object");
        }

        where = $"{where} case_upload";
        if (!Wire.TryParse<CaseUploadStatus>(Json.AsString(raw["status"]), out var status))
        {
            throw Fail(where, "status must be one of ('unverified', 'legacy_txt', 'xml')");
        }

        var verifiedAt = Date(raw["verified_at"], where, "verified_at");
        if (status != CaseUploadStatus.Unverified && verifiedAt is null)
        {
            throw Fail(where, "a verified Case Upload status needs verified_at");
        }

        return new CaseUpload(status, OptionalStr(raw, "kind", where), verifiedAt, Notes(raw, "note", where));
    }

    private static CourtDistrict ParseDistrict(JsonObject raw, string where)
    {
        var code = StrField(raw, "code", where);
        where = $"{where} {code}";
        if (!Code().IsMatch(code))
        {
            throw Fail(where, "code must be the four-letter CM/ECF id");
        }

        var state = StrField(raw, "state", where);
        if (!StatesWithFips.Contains(state))
        {
            throw Fail(where, $"state '{state}' is not a launch state (ADR 0017)");
        }

        if (!Json.TryGetWholeNumber(raw["circuit"], out var circuit))
        {
            throw Fail(where, "circuit must be an integer");
        }

        var sources = Sources(raw["sources"], where);
        var facts = new Facts(sources.Select(source => source.Id).ToHashSet(StringComparer.Ordinal), where);
        if (raw["divisions"] is not JsonArray divisionsRaw || divisionsRaw.Count == 0)
        {
            throw Fail(where, "divisions missing or empty");
        }

        var divisions = divisionsRaw.Select(division => ParseDivision(division, where, facts, state)).ToValueCollection();
        if (divisions.Select(division => division.Code).Distinct(StringComparer.Ordinal).Count() != divisions.Count)
        {
            throw Fail(where, "duplicate division codes");
        }

        var fipsSeen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var division in divisions)
        {
            foreach (var county in division.Counties)
            {
                if (fipsSeen.TryGetValue(county.Fips, out var other))
                {
                    throw Fail(where, $"county {county.Fips} is in both {other} and {division.Code}");
                }

                fipsSeen[county.Fips] = division.Code;
            }
        }

        foreach (var section in new[] { "cmecf", "pdf", "matrix", "registration" })
        {
            if (raw[section] is not JsonObject)
            {
                throw Fail(where, $"{section} is not an object");
            }
        }

        var cmecfRaw = raw["cmecf"]!.AsObject();
        var pdfRaw = raw["pdf"]!.AsObject();
        var matrixRaw = raw["matrix"]!.AsObject();
        var registrationRaw = raw["registration"]!.AsObject();
        var cmecf = new Facts(facts.SourceIds, $"{where} cmecf");
        var pdf = new Facts(facts.SourceIds, $"{where} pdf");
        var matrix = new Facts(facts.SourceIds, $"{where} matrix");
        var registration = new Facts(facts.SourceIds, $"{where} registration");

        return new CourtDistrict(
            Code: code,
            CourtId: OptionalStr(raw, "court_id", where),
            Name: StrField(raw, "name", where),
            State: state,
            Circuit: (int)circuit,
            Website: StrField(raw, "website", where),
            Divisions: divisions,
            Cmecf: new CmEcf(cmecf.String(cmecfRaw, "live_url"), cmecf.String(cmecfRaw, "training_url"), cmecf.String(cmecfRaw, "help_desk"), cmecf.String(cmecfRaw, "release")),
            Pdf: new PdfRules(pdf.Integer(pdfRaw, "max_bytes"), pdf.Boolean(pdfRaw, "text_searchable_required"), pdf.Boolean(pdfRaw, "flatten_required"), pdf.Boolean(pdfRaw, "pdf_a_required")),
            Matrix: new MatrixRules(
                matrix.Integer(matrixRaw, "max_line_chars"),
                matrix.Integer(matrixRaw, "max_creditor_lines"),
                matrix.Integer(matrixRaw, "blank_lines_between"),
                matrix.Integer(matrixRaw, "pad_to_lines"),
                matrix.Integer(matrixRaw, "name_line_chars"),
                matrix.Boolean(matrixRaw, "comma_after_city"),
                matrix.Boolean(matrixRaw, "case_number_header_when_separate"),
                matrix.Boolean(matrixRaw, "certification_required")),
            Opening: ParseOpening(raw["opening"], where, facts),
            CaseUpload: ParseCaseUpload(raw["case_upload"], where),
            Registration: new Registration(registration.Boolean(registrationRaw, "training_required"), Notes(registrationRaw, "notes", $"{where} registration")),
            Sources: sources,
            Notes: Notes(raw, "notes", where),
            Chapter13Plan: Chapter13Plan(raw, where, facts));
    }

    private static JsonObject LoadJson(IReadOnlyDictionary<string, string> files, string path, string where)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        if (!files.TryGetValue(path, out var text))
        {
            throw Fail(where, $"{name}: No such file");
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(text);
        }
        catch (JsonException error)
        {
            throw Fail(where, $"{name}: {error.Message}");
        }

        return parsed as JsonObject ?? throw Fail(where, $"{name} is not a JSON object");
    }

    private static CourtsRelease LoadRelease(IReadOnlyDictionary<string, string> files, string releasePath, string dirName)
    {
        var where = $"{CourtRegistry.SeriesId}/{dirName}";
        var match = DirName().Match(dirName);
        if (!match.Success)
        {
            throw Fail(where, "directory name is not <effective_date>[+<sequence>]");
        }

        var effective = DateOnly.ParseExact(match.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var sequence = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 1;
        if (sequence < 1)
        {
            throw Fail(where, "sequence must be >= 1");
        }

        var manifest = LoadJson(files, $"{releasePath}/manifest.json", where);
        if (Json.AsString(manifest["series_id"]) != CourtRegistry.SeriesId)
        {
            throw Fail(where, $"manifest series_id {Repr(manifest["series_id"])}");
        }

        if (Json.AsString(manifest["effective_date"]) != effective.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
        {
            throw Fail(where, "manifest effective_date disagrees with the path");
        }

        if (!Json.TryGetWholeNumber(manifest["sequence"], out var manifestSequence) || manifestSequence != sequence)
        {
            throw Fail(where, "manifest sequence disagrees with the path");
        }

        if (manifest["source"] is not JsonObject source)
        {
            throw Fail(where, "manifest source missing");
        }

        var notes = Json.AsString(manifest["notes"]);
        if (notes is null || TextRules.Strip(notes).Length == 0)
        {
            throw Fail(where, "manifest notes missing — say what this release is");
        }

        var districtPrefix = $"{releasePath}/districts/";
        var districtFiles = files.Keys
            .Where(path => path.StartsWith(districtPrefix, StringComparison.Ordinal) && path.EndsWith(".json", StringComparison.Ordinal)
                && !path[districtPrefix.Length..].Contains('/', StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (districtFiles.Count == 0)
        {
            throw Fail(where, "no district records under districts/");
        }

        var districts = new List<CourtDistrict>();
        foreach (var path in districtFiles)
        {
            var record = ParseDistrict(LoadJson(files, path, where), where);
            var fileName = path[districtPrefix.Length..];
            if (fileName != $"{record.Code}.json")
            {
                throw Fail(where, $"{fileName} holds the record for '{record.Code}'");
            }

            districts.Add(record);
        }

        return new CourtsRelease(
            CourtRegistry.SeriesId,
            effective,
            sequence,
            StrField(source, "url", where),
            Date(source["published"], where, "source.published"),
            OptionalStr(source, "sha256", where),
            notes,
            districts.ToValueCollection());
    }

    private static string QuotedRepr(JsonNode? value)
    {
        var text = Json.AsString(value);
        return text is null ? Repr(value) : "'" + text.Replace("\n", "\\n", StringComparison.Ordinal) + "'";
    }

    [GeneratedRegex(@"^(?:form/b[0-9a-z]+|creditor_matrix|signature_instrument|matrix_certification|local_form/[A-Za-z0-9-]+)$")]
    private static partial Regex DocumentKey();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,80}\.(?:pdf|txt)\z")]
    private static partial Regex FileName();

    [GeneratedRegex("^[a-z]{4}$")]
    private static partial Regex Code();

    [GeneratedRegex("^[a-z][a-z0-9-]{1,40}$")]
    private static partial Regex DivisionCode();

    [GeneratedRegex(@"^\d{5}$")]
    private static partial Regex Fips();

    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2})(?:\+(\d+))?$")]
    private static partial Regex DirName();

    /// <summary>Parses fact wrappers against one record's source list, so a fact citing an unlisted source fails the load.</summary>
    private sealed class Facts(IReadOnlySet<string> sourceIds, string where)
    {
        public IReadOnlySet<string> SourceIds { get; } = sourceIds;

        public (JsonNode? Value, Fact<object> Fact) Raw(JsonObject data, string key)
        {
            var here = $"{where} {key}";
            if (data[key] is not JsonObject wrapper)
            {
                throw Fail(here, "must be a fact object {value, status, ...}");
            }

            if (!Wire.TryParse<FactStatus>(Json.AsString(wrapper["status"]), out var status))
            {
                throw Fail(here, "status must be one of ('verified', 'unverified')");
            }

            var source = OptionalStr(wrapper, "source", here);
            if (source is not null && !SourceIds.Contains(source))
            {
                throw Fail(here, $"source '{source}' is not in the record's sources");
            }

            var verifiedAt = Date(wrapper["verified_at"], here, "verified_at");
            if (status == FactStatus.Verified && (source is null || verifiedAt is null))
            {
                throw Fail(here, "a verified fact needs a source and verified_at");
            }

            return (wrapper["value"], new Fact<object>(null, status, source, verifiedAt, Notes(wrapper, "note", here)));
        }

        public Fact<string> String(JsonObject data, string key)
        {
            var (value, fact) = Raw(data, key);
            string? text = null;
            if (value is not null)
            {
                text = Json.AsString(value) ?? throw Fail($"{where} {key}", "value must be a string or null");
            }

            return new Fact<string>(text, fact.Status, fact.Source, fact.VerifiedAt, fact.Note);
        }

        public Fact<TEnum?> Choice<TEnum>(JsonObject data, string key)
            where TEnum : struct, Enum
        {
            var fact = String(data, key);
            TEnum? member = null;
            if (fact.Value is not null)
            {
                if (!Wire.TryParse<TEnum>(fact.Value, out var parsed))
                {
                    var allowed = string.Join(", ", Wire.Names<TEnum>().Select(name => $"'{name}'"));
                    throw Fail($"{where} {key}", $"value must be one of ({allowed})");
                }

                member = parsed;
            }

            return new Fact<TEnum?>(member, fact.Status, fact.Source, fact.VerifiedAt, fact.Note);
        }

        public Fact<long?> Integer(JsonObject data, string key)
        {
            var (value, fact) = Raw(data, key);
            long? number = null;
            if (value is not null)
            {
                if (!Json.TryGetWholeNumber(value, out var parsed))
                {
                    throw Fail($"{where} {key}", "value must be an integer or null");
                }

                number = parsed;
            }

            return new Fact<long?>(number, fact.Status, fact.Source, fact.VerifiedAt, fact.Note);
        }

        public Fact<bool?> Boolean(JsonObject data, string key)
        {
            var (value, fact) = Raw(data, key);
            bool? flag = null;
            if (value is not null)
            {
                flag = Json.AsBool(value) ?? throw Fail($"{where} {key}", "value must be a boolean or null");
            }

            return new Fact<bool?>(flag, fact.Status, fact.Source, fact.VerifiedAt, fact.Note);
        }

        public (JsonObject? Value, Fact<object> Fact) Mapping(JsonObject data, string key)
        {
            var (value, fact) = Raw(data, key);
            if (value is not null && value is not JsonObject)
            {
                throw Fail($"{where} {key}", "value must be an object or null");
            }

            return ((JsonObject?)value, fact);
        }
    }
}
