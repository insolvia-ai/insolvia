using System.Globalization;
using System.Text.RegularExpressions;
using Insolvia.UstTool;

// The UST means-testing datasets' tool (#585, ADR 0029 rule 4):
//
//   ingest medians          --period 20260401 --effective 2026-04-01
//   ingest national         --period 20260715 --effective 2026-07-15
//   ingest local            --period 20260715 --effective 2026-07-15 --msa-file msa-counties.json
//   ingest ch13-multipliers --period 20260715 --effective 2026-07-15
//       [--sequence N] [--accessed YYYY-MM-DD] [--out-root DIR] [--from-dir DIR]
//   check [--registry DIR] [--today YYYY-MM-DD]
//
// Run from anywhere in the checkout:
//   dotnet run --project backend/src/Insolvia.UstTool -- <command> …
//
// The local standards' transportation MSA county definitions are NOT in any
// spreadsheet — the UST publishes them only on the per-region HTML pages
// (IRS_Trans_Exp_Stds_SO.htm and siblings), so they are curated by hand into
// the small JSON file --msa-file names, embedded verbatim. --from-dir reads
// the period's spreadsheets from a directory (each by its file name) instead
// of justice.gov: re-ingest a recording, or work offline.
//
// After an ingest, edit the manifest's `notes` if anything about the source
// was odd, then run `dotnet test tests/Insolvia.Engines.Tests` in backend/ —
// UstDataTests validates every committed release, and `Backend (.NET)` fails
// the PR on a malformed one. (The tool has already loaded it through the
// engine before writing it.)

const string Usage = "usage: ingest {medians|national|local|ch13-multipliers} --period YYYYMMDD --effective YYYY-MM-DD "
    + "[--sequence N] [--accessed YYYY-MM-DD] [--out-root DIR] [--msa-file FILE] [--from-dir DIR] | check [--registry DIR] [--today YYYY-MM-DD]";

if (args.Length == 0)
{
    Console.Error.WriteLine(Usage);
    return 2;
}

Dictionary<string, string> options;
try
{
    options = Options(args.AsSpan(args[0] == "ingest" ? 2 : 1).ToArray());
}
catch (ArgumentException error)
{
    Console.Error.WriteLine($"{error.Message}\n{Usage}");
    return 2;
}

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
// justice.gov answers 403 to a client with no User-Agent; identify as a plain
// client the way curl does.
http.DefaultRequestHeaders.UserAgent.ParseAdd("curl/8");

switch (args[0])
{
    case "check":
        {
            string registry;
            try
            {
                registry = options.GetValueOrDefault("registry") ?? RegistryRoot();
            }
            catch (IngestException error)
            {
                Console.Error.WriteLine($"could not verify freshness: {error.Message}");
                return 2;
            }

            var today = options.TryGetValue("today", out var day) ? DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture) : DateOnly.FromDateTime(DateTime.Now);
            return await FreshnessCheck.RunAsync(
                registry,
                today,
                url => http.GetByteArrayAsync(new Uri(url)),
                Console.Out,
                Console.Error);
        }

    case "ingest" when args.Length >= 2:
        {
            try
            {
                var period = Required(options, "period");
                if (!Regex.IsMatch(period, @"\A\d{8}\z"))
                {
                    throw new IngestException("--period must be YYYYMMDD");
                }

                var effective = Required(options, "effective");
                if (!DateOnly.TryParseExact(effective, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                {
                    throw new IngestException("--effective must be YYYY-MM-DD");
                }

                var ingestArgs = new IngestArgs(
                    period,
                    effective,
                    options.TryGetValue("sequence", out var sequence) ? int.Parse(sequence, CultureInfo.InvariantCulture) : 1,
                    options.GetValueOrDefault("accessed") ?? DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    options.GetValueOrDefault("out-root") ?? RegistryRoot(),
                    options.GetValueOrDefault("msa-file"));
                Fetcher fetch = options.TryGetValue("from-dir", out var fromDir)
                    ? url =>
                    {
                        var path = Path.Combine(fromDir, url[(url.LastIndexOf('/') + 1)..]);
                        Console.WriteLine($"  reading {path} (for {url})");
                        return File.ReadAllBytesAsync(path);
                    }
                : url =>
                {
                    Console.WriteLine($"  fetching {url}");
                    return http.GetByteArrayAsync(new Uri(url));
                };
                var release = args[1] switch
                {
                    "medians" => await Ingest.MediansAsync(ingestArgs, fetch),
                    "national" => await Ingest.NationalAsync(ingestArgs, fetch),
                    "local" => await Ingest.LocalAsync(ingestArgs, fetch),
                    "ch13-multipliers" => await Ingest.Ch13MultipliersAsync(ingestArgs, fetch),
                    _ => throw new IngestException($"unknown dataset {args[1]}\n{Usage}"),
                };
                Console.WriteLine($"  wrote {Ingest.Write(ingestArgs.OutRoot, release)}");
                return 0;
            }
            catch (IngestException error)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        }

    default:
        Console.Error.WriteLine(Usage);
        return 2;
}

static Dictionary<string, string> Options(string[] rest)
{
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i < rest.Length; i++)
    {
        if (!rest[i].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"unexpected argument {rest[i]}");
        }

        var name = rest[i][2..];
        var equals = name.IndexOf('=', StringComparison.Ordinal);
        if (equals >= 0)
        {
            options[name[..equals]] = name[(equals + 1)..];
        }
        else if (i + 1 < rest.Length)
        {
            options[name] = rest[++i];
        }
        else
        {
            throw new ArgumentException($"--{name} needs a value");
        }
    }

    return options;
}

static string Required(Dictionary<string, string> options, string name) =>
    options.TryGetValue(name, out var value) ? value : throw new IngestException($"--{name} is required");

// The canonical registry (ADR 0014) in this checkout: found by walking up from
// the working directory, then from the tool's own build output.
static string RegistryRoot()
{
    foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "backend", "src", "Insolvia.Engines", "Regulatory");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    throw new IngestException("no backend/src/Insolvia.Engines/Regulatory above this directory; pass --out-root/--registry");
}
