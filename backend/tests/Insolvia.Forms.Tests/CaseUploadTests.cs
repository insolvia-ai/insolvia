using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Insolvia.Core;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Courts;
using Insolvia.Core.Fields;
using Insolvia.Core.Memory;
using Insolvia.Core.Ports;
using Insolvia.Core.TaxIds;
using Insolvia.Forms.Tests.Support;

namespace Insolvia.Forms.Tests;

// test_case_upload.py, translated (ADR 0024 build PR 9): Debtor.txt for the
// reference case validates against the encoded AO spec, every kind of fault
// is caught, and the full SSN never leaves the file. The reference case is
// the API's own (reference-ch7, placed in FLMB like test_filing_approval's
// `in_court`); its tax ids are the SSA's advertising block, never issued.
public sealed class CaseUploadTests
{
    // test_filing_approval.AS_OF — the approval world's fixed far-future day.
    private static readonly DateOnly AsOf = new(2099, 1, 15);
    private static readonly CaseUploadSpec Spec = CaseUploadSpecs.Resolve(AsOf);
    private static readonly CourtsRelease Registry = CourtRegistry.Shipped.Latest;

    private static readonly string[] RecordKinds = ["stat", "debt", "alas"];
    private static readonly int[] MoneyFields = [25, 26, 27, 28, 29, 30, 31, 33];
    private static readonly string[] YesNo = ["y", "n"];
    private static readonly string[] SsnForms = ["987654321", "987-65-4321", "987654322", "987-65-4322"];

    internal static CaseData InCourt(CaseData data, CourtsRelease registry, string code = "flmb")
    {
        var district = registry.District(code)!;
        return data with { Case = data.Case with { Court = code, Division = district.Divisions[0].Code, District = district.Name } };
    }

    private static CaseData Data() => InCourt(CaseDatas.Named("reference-ch7"), Registry);

    private static CaseUploadFile Build(CaseData? data = null) => CaseUploads.Build(data ?? Data(), AsOf, Registry, Spec);

    private static List<List<string>> Lines(CaseUploadFile? file = null)
    {
        var content = Encoding.ASCII.GetString((file ?? Build()).Content);
        var lines = content.Split('\n');
        return [.. lines[..^1].Select(line => line.Split('|').ToList())];
    }

    private static string Joined(IEnumerable<List<string>> rows) => string.Concat(rows.Select(row => string.Join('|', row) + "\n"));

    private static HashSet<(string Record, int Field, string Code)> Codes(IEnumerable<CaseUploadProblem> problems) =>
        [.. problems.Select(p => (p.Record, p.Field, p.Code))];

    private static HashSet<(string Record, int Field, string Code)> ProblemsWith(Action<List<List<string>>> mutate)
    {
        var rows = Lines();
        mutate(rows);
        return Codes(CaseUploadSpecs.Validate(Joined(rows), Spec));
    }

    // ── The spec, as data ──

    [Fact]
    public void The_spec_encodes_every_field_of_the_three_records()
    {
        Assert.Equal([80, 21, 8], RecordKinds.Select(kind => Spec.Records[kind].Fields.Count));
        foreach (var record in Spec.Records.Values)
        {
            Assert.Equal(Enumerable.Range(1, record.Fields.Count), record.Fields.Select(field => field.Number));
        }
    }

    [Fact]
    public void The_spec_cites_its_source_and_records_its_ambiguities()
    {
        var files = CaseUploadSpecs.EmbeddedFiles();
        var manifest = JsonNode.Parse(files["filing/case-upload/2022-03-01/manifest.json"])!;
        var raw = JsonNode.Parse(files["filing/case-upload/2022-03-01/spec.json"])!;

        Assert.StartsWith("https://pacer.uscourts.gov/", manifest["source"]!["url"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("S22", raw["source"]!.GetValue<string>(), StringComparison.Ordinal);
        var ids = raw["ambiguities"]!.AsArray().Select(a => a!["id"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        Assert.Superset(new HashSet<string>(["trailing-delimiter", "signed-money-width", "alias-type-table"], StringComparer.Ordinal), ids);
        Assert.Equal("filing/case-upload@2022-03-01", Spec.ReleaseId);
    }

    [Fact]
    public void A_malformed_spec_is_refused_whole()
    {
        var files = CaseUploadSpecs.EmbeddedFiles();
        var manifest = Encoding.UTF8.GetString(files["filing/case-upload/2022-03-01/manifest.json"]);
        var spec = JsonNode.Parse(files["filing/case-upload/2022-03-01/spec.json"])!.AsObject();
        spec["records"]!["debt"]!["fields"]!.AsArray().RemoveAt(20);

        var error = Assert.Throws<Engines.MalformedReleaseException>(() => CaseUploadSpecs.Load("2022-03-01", manifest, spec.ToJsonString()));

        Assert.Equal("malformed case-upload spec 2022-03-01: debt must have 21 fields", error.Message);
        Assert.Throws<Engines.EngineLookupException>(() => CaseUploadSpecs.Resolve(new DateOnly(2022, 2, 28)));
    }

    // ── The done-when: the reference case validates ──

    [Fact]
    public void The_reference_case_debtor_txt_validates_against_the_spec()
    {
        var file = Build();

        Assert.Equal("Debtor.txt", file.FileName);
        Assert.Empty(CaseUploadSpecs.Validate(file.Content, Spec));
    }

    [Fact]
    public void The_reference_case_records()
    {
        var lines = Lines();
        var (stat, debtor1, debtor2, aliases) = (lines[0], lines[1], lines[2], lines[3..]);

        Assert.Equal(80, stat.Count); // no trailing delimiter (spec ambiguity)
        Assert.Equal(22, debtor1.Count); // a trailing delimiter, as sampled
        Assert.Equal(string.Empty, debtor1[^1]);
        Assert.Equal(["stat", "", "", "", "i", "o", "", "7", "i", "c", "v"], stat[..11]);

        // B101 lines 18-20: 1-49 creditors, the $100M-500M asset bracket, the
        // $50,001-100,000 liability bracket.
        Assert.Equal(["A", "H", "B"], stat[13..16]);
        Assert.Equal("y", stat[23]); // a prior case within 8 years (B101 line 9)
        Assert.Equal(["debt", "db", "Ada", "Quinn", "Lovelace", "", "", "987-65-4321"], debtor1[..8]);
        Assert.Equal("12-3456789", debtor1[8]);

        // FLMB's first division (Jacksonville, office 3); Hillsborough's FIPS.
        Assert.Equal("3", debtor1[9]);
        Assert.Equal("12057", debtor1[16]);

        // The mailing address, where the court's notices go.
        Assert.Equal(["4501 Postal Way", "PO Box 99", "", "Tampa", "FL", "33602"], debtor1[10..16]);
        Assert.Equal(["jdb", "Ben", "", "Lovelace", "", "Jr.", "987-65-4322"], debtor2[1..8]);
        Assert.Equal(
            [["alas", "db", "aka", "Ada", "", "Byron"], ["alas", "db", "aka", "", "", "Ada's Analytical Engines"]],
            aliases.Select(alias => alias[..6]));
    }

    [Fact]
    public void The_statistics_are_the_forms_figures()
    {
        var stat = Lines()[0];

        var money = MoneyFields.Select(n => stat[n - 1]);
        Assert.All(money, value => Assert.True(value.Length > 0 && value.Count(c => c == '.') == 1, value));
        Assert.NotEmpty(stat[40 - 1]); // 106J line 23c
        Assert.Contains(stat[21 - 1], YesNo);
    }

    [Fact]
    public void The_file_is_deterministic() => Assert.Equal(Build().Content, Build().Content);

    // ── Negative: each field type, width, requirement and code ──

    public static TheoryData<int, int, string, string> FieldFaults() => new()
    {
        { 0, 25, "12.5", "format" }, // money: two decimals
        { 0, 25, "-1.00", "format" }, // money: unsigned
        { 0, 25, "1,000.00", "format" }, // money: no grouping
        { 0, 40, "-1234567890123.00", "width" }, // signed money past 15
        { 0, 40, "12", "format" }, // signed money
        { 0, 47, "1x", "format" }, // integer
        { 0, 15, "J", "code" }, // J is retired for assets
        { 0, 9, "x", "code" }, // fee status
        { 0, 6, "hh", "code" }, // letters repeated
        { 0, 12, "y", "width" }, // not used: width 0
        { 0, 19, "AB", "not_blank" }, // reserved for future use
        { 0, 72, "2026-0042", "format" }, // case number
        { 0, 76, "12A4", "format" }, // NAICS
        { 1, 8, "987654321", "format" }, // SSN without dashes
        { 1, 9, "123456789", "format" }, // EIN
        { 1, 10, "33", "width" }, // office code is one character
        { 1, 10, "%", "format" },
        { 1, 17, "1205", "format" }, // county code is FIPS-5
        { 1, 3, new string('A', 21), "width" }, // first name 20
        { 1, 15, "FLA", "width" }, // state 2
        { 1, 2, "dbxx", "width" },
        { 3, 3, "dba", "code" }, // alias type outside the attested table
    };

    [Theory]
    [MemberData(nameof(FieldFaults))]
    public void Each_field_type_is_checked(int record, int index, string value, string code)
    {
        var found = ProblemsWith(rows => rows[record][index - 1] = value);

        var kind = new[] { "stat", "debt", "debt", "alas", "alas" }[record];
        Assert.Contains((kind, index, code), found);
    }

    public static TheoryData<int, int> RequiredFields() => new()
    {
        { 0, 5 }, { 0, 8 }, { 0, 9 }, { 0, 10 }, { 0, 11 }, { 0, 14 }, { 0, 17 }, { 0, 21 }, { 0, 24 },
        { 1, 3 }, { 1, 5 }, { 1, 8 }, { 1, 10 }, { 1, 11 }, { 1, 17 }, { 3, 6 },
    };

    [Theory]
    [MemberData(nameof(RequiredFields))]
    public void A_missing_required_field_is_caught(int record, int index)
    {
        var kind = new[] { "stat", "debt", "debt", "alas" }[record];

        Assert.Contains((kind, index, "required"), ProblemsWith(rows => rows[record][index - 1] = string.Empty));
    }

    [Fact]
    public void A_wrong_field_count_is_caught_the_way_the_court_refuses_it()
    {
        Assert.Contains(("stat", 0, "field_count"), ProblemsWith(rows => rows[0].RemoveAt(rows[0].Count - 1)));
        Assert.Contains(("debt", 0, "field_count"), ProblemsWith(rows => rows[1].RemoveAt(rows[1].Count - 1)));
    }

    [Fact]
    public void Cross_field_rules()
    {
        Assert.Contains(("stat", 9, "rule"), ProblemsWith(rows => (rows[0][7], rows[0][8]) = ("13", "w")));
        Assert.Contains(("stat", 6, "rule"), ProblemsWith(rows => (rows[0][4], rows[0][5]) = ("c", "hr")));
        Assert.Contains(("alas", 2, "role"), ProblemsWith(rows =>
        {
            rows[3][1] = "jdb";
            rows.RemoveAt(2);
        }));
    }

    [Fact]
    public void The_file_shape_is_checked()
    {
        var rows = Lines();
        var text = Joined(rows);

        Assert.Contains((string.Empty, 0, "separator"), Codes(CaseUploadSpecs.Validate(text.TrimEnd('\n'), Spec)));
        Assert.Contains((string.Empty, 0, "charset"), Codes(CaseUploadSpecs.Validate(text.Replace("\n", "\r\n", StringComparison.Ordinal), Spec)));
        Assert.Contains((string.Empty, 0, "charset"), Codes(CaseUploadSpecs.Validate(Encoding.UTF8.GetBytes("stat|é\n"), Spec)));
        var swapped = Joined([rows[1], rows[0], .. rows[2..]]);
        Assert.Contains(("stat", 0, "order"), Codes(CaseUploadSpecs.Validate(swapped, Spec)));
        Assert.Contains((string.Empty, 0, "record_type"), Codes(CaseUploadSpecs.Validate(text + "alias|db|aka||||||\n", Spec)));
    }

    // ── What cannot be built ──

    [Fact]
    public void A_county_outside_the_district_cannot_land()
    {
        var data = Data();
        var first = data.Debtors[0];
        var moved = first with { Body = first.Body with { ResidenceAddress = first.Body.ResidenceAddress with { County = "Fulton" } } };

        var raised = Assert.Throws<CaseUploadException>(() => Build(data with { Debtors = [moved, .. data.Debtors.Skip(1)] }));

        Assert.Contains(("debt", 17, "county"), Codes(raised.Problems));
    }

    [Fact]
    public void A_missing_tax_id_cannot_land()
    {
        var raised = Assert.Throws<CaseUploadException>(() => Build(Data() with { TaxIds = new Dictionary<string, string>(StringComparer.Ordinal) { ["debtor_1"] = "987654321" } }));

        Assert.Contains(("debt", 8, "tax_id"), Codes(raised.Problems));
    }

    [Fact]
    public void A_value_too_wide_is_refused_by_the_validator()
    {
        var data = Data();
        var first = data.Debtors[0];
        var wide = first with { Body = first.Body with { MailingAddress = new Address(Line1: new string('1', 41), City: "Tampa", State: "FL", PostalCode: "33602") } };

        var raised = Assert.Throws<CaseUploadException>(() => Build(data with { Debtors = [wide, .. data.Debtors.Skip(1)] }));

        Assert.Contains(("debt", 11, "width"), Codes(raised.Problems));
    }

    [Fact]
    public void Accents_fold_to_ascii()
    {
        var data = Data();
        var first = data.Debtors[0];
        var accented = first with { Body = first.Body with { Name = first.Body.Name with { Given = "Adá" } } };

        var file = Build(data with { Debtors = [accented, .. data.Debtors.Skip(1)] });

        Assert.Equal("Ada", Lines(file)[1][2]);
    }

    [Fact]
    public void A_character_the_file_cannot_carry_is_refused_by_name_never_by_value()
    {
        var data = Data();
        var first = data.Debtors[0];
        var piped = first with { Body = first.Body with { Name = first.Body.Name with { Surname = "Love|lace" } } };

        var raised = Assert.Throws<CaseUploadException>(() => Build(data with { Debtors = [piped, .. data.Debtors.Skip(1)] }));

        Assert.Contains(("debt", 5, "charset"), Codes(raised.Problems));
        Assert.DoesNotContain("Love", raised.Message, StringComparison.Ordinal);
    }

    // ── The SSN never leaves the file ──

    private static void AssertNoSsn(string text)
    {
        foreach (var form in SsnForms)
        {
            Assert.DoesNotContain(form, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_composition_logs_one_taxid_read_per_debtor_with_its_purpose()
    {
        // The stores as the API's packet tests build them: the tax ids SEALED,
        // so only the logged read can put the digits in the file.
        var data = Data();
        var (debtors, entities, taxIds, cipher) = await SealedStoresAsync(data);
        var log = new MemoryAccessLog();

        var file = await CaseUploads.ForFilingAsync(data.Case, "attorney-0001", AsOf, debtors, entities, taxIds, cipher, log, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("987-65-4321", Encoding.ASCII.GetString(file.Content), StringComparison.Ordinal);
        Assert.Equal(Build().Content, file.Content);
        var reads = log.Events.Where(e => e.Action == AccessAction.TaxIdRead).Select(e => (e.Principal, e.Purpose, e.FilingRole));
        Assert.Equal([("attorney-0001", "case_upload", "debtor_1"), ("attorney-0001", "case_upload", "debtor_2")], reads);
        AssertNoSsn(file.ToString());
        AssertNoSsn(JsonSerializer.Serialize(log.Events));
        AssertNoSsn(string.Join(",", log.Events));
    }

    [Fact]
    public void No_problem_or_exception_carries_a_value()
    {
        var rows = Lines();
        rows[1][7] = "987-65-43210"; // an SSN one digit too long
        rows[1][2] = new string('A', 21);

        var problems = CaseUploadSpecs.Validate(Joined(rows), Spec);

        Assert.NotEmpty(problems);
        var error = new CaseUploadException(problems);
        foreach (var text in new[] { error.Message, error.ToString() }.Concat(problems.Select(p => p.ToString())))
        {
            AssertNoSsn(text);
            Assert.DoesNotContain(new string('A', 21), text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Nothing_that_answers_a_client_composes_the_file()
    {
        // The file is streamed to the court by the filing worker and never
        // stored: no project but Forms (which owns it) and the filing worker
        // names the builder, its file or its spec.
        var src = SourceRoot();
        var offenders = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(src, path).Replace('\\', '/'))
            .Where(path => !path.Contains("/obj/", StringComparison.Ordinal) && !path.Contains("/bin/", StringComparison.Ordinal))
            .Where(path => path is not ("Insolvia.Forms/CaseUploads.cs" or "Insolvia.Forms/CaseUploadSpec.cs") && !path.StartsWith("Insolvia.Filing/", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(Path.Combine(src, path)).Contains("CaseUpload", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(Path.Combine(src, path)) is var text && (text.Contains("CaseUploads.", StringComparison.Ordinal) || text.Contains("CaseUploadFile", StringComparison.Ordinal) || text.Contains("CaseUploadSpecs", StringComparison.Ordinal)))
            .ToList();

        Assert.Empty(offenders);
    }

    // ── helpers ──

    internal static string SourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "backend", "src");
            if (Directory.Exists(Path.Combine(candidate, "Insolvia.Forms")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("backend/src");
    }

    /// <summary>The case in memory stores, each debtor's tax id SEALED under its firm and ref — the read is the only way back to the digits.</summary>
    internal static async Task<(MemoryDebtorStore Debtors, MemoryCaseEntityStore Entities, MemoryTaxIdStore TaxIds, ITaxIdCipher Cipher)> SealedStoresAsync(CaseData data)
    {
        var debtors = new MemoryDebtorStore();
        var entities = new MemoryCaseEntityStore();
        var taxIds = new MemoryTaxIdStore();
        var cipher = new TestTaxIdCipher();
        foreach (var debtor in data.Debtors)
        {
            await debtors.CreateAsync(debtor);
            if (debtor.TaxId is { } taxId)
            {
                var envelope = await cipher.SealAsync(data.TaxIds[debtor.FilingRole.ToWire()], TaxIdRules.EncryptionContext(data.Case.FirmId, taxId.Ref));
                await taxIds.PutAsync(data.Case.Id, new SealedTaxId(taxId.Ref, data.Case.FirmId, taxId.Kind, taxId.LastFour, envelope, CaseDatas.EntityTime, CaseDatas.EntityTime));
            }
        }

        foreach (var (_, members) in data.Collections())
        {
            foreach (var entity in members)
            {
                await entities.CreateAsync(entity);
            }
        }

        return (debtors, entities, taxIds, cipher);
    }

    /// <summary>A stand-in cipher: the plaintext masked and bound to its context, so an envelope opens only under the context it was sealed with.</summary>
    private sealed class TestTaxIdCipher : ITaxIdCipher
    {
        public Task<Envelope> SealAsync(string plaintext, IReadOnlyDictionary<string, string> context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Envelope("test/1", Convert.ToBase64String([.. Encoding.ASCII.GetBytes(plaintext).Select(b => (byte)(b ^ 0x5A))]), Bind(context), string.Empty));

        public Task<string> OpenAsync(Envelope envelope, IReadOnlyDictionary<string, string> context, CancellationToken cancellationToken = default) =>
            envelope.Nonce == Bind(context)
                ? Task.FromResult(Encoding.ASCII.GetString([.. Convert.FromBase64String(envelope.Ciphertext).Select(b => (byte)(b ^ 0x5A))]))
                : throw new CryptographicException("context mismatch");

        private static string Bind(IReadOnlyDictionary<string, string> context) =>
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(";", context.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}")))));
    }
}

// The Case Upload half of the parity gate: Debtor.txt for the four parity
// case files, built by Insolvia.Forms, byte-identical to what services/api's
// case_upload.build_debtor_txt wrote (Parity/debtor_txt.py ->
// Parity/debtor-txt/<case>.Debtor.txt) — encoding, line endings, delimiters
// and blanks included. And the embedded spec, pinned to the Python copy.
public sealed class CaseUploadParityTests
{
    private static readonly DateOnly AsOf = new(2026, 10, 1);

    public static TheoryData<string> Cases() => ["reference-ch7", "reference-ch13", "chapter-7-sample", "chapter-13-sample"];

    [Theory]
    [MemberData(nameof(Cases))]
    public void Debtor_txt_is_byte_identical_to_the_python(string name)
    {
        var registry = CourtRegistry.Shipped.Resolve(AsOf);
        var data = CaseDatas.Named(name);
        if (data.Case.Court is null)
        {
            data = CaseUploadTests.InCourt(data, registry);
        }

        var file = CaseUploads.Build(data, AsOf, registry, CaseUploadSpecs.Resolve(AsOf));

        var expected = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "DebtorTxt", $"{name}.Debtor.txt"));
        Assert.Equal(Encoding.ASCII.GetString(expected), Encoding.ASCII.GetString(file.Content));
        Assert.Equal(expected, file.Content);
    }

    // The embedded copies' sha256, as services/api's files hash today.
    public static TheoryData<string, string> Pinned() => new()
    {
        { "filing/case-upload/2022-03-01/manifest.json", "4212d0a06f13f649735f43799a6519d0c91fefbef4b6b8fb6e333fa888865a89" },
        { "filing/case-upload/2022-03-01/spec.json", "65aaf9c11308a897dfecb31226fb42a1d2b8bb7e203511c30f75e5620d9266ab" },
    };

    [Theory]
    [MemberData(nameof(Pinned))]
    public void The_embedded_spec_is_the_python_copy_byte_for_byte(string path, string sha256)
    {
        var embedded = CaseUploadSpecs.EmbeddedFiles();

        Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(embedded[path])));
        var python = Path.Combine(CaseUploadTests.SourceRoot(), "..", "..", "services", "api", "src", "insolvia_api", "regulatory", path);
        Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(python))));
    }

    [Fact]
    public void Every_python_case_upload_file_is_embedded()
    {
        var root = Path.Combine(CaseUploadTests.SourceRoot(), "..", "..", "services", "api", "src", "insolvia_api", "regulatory");
        var onDisk = Directory.GetFiles(Path.Combine(root, "filing"), "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);

        Assert.Equal(onDisk, CaseUploadSpecs.EmbeddedFiles().Keys);
    }
}
