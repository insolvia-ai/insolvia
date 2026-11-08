using Insolvia.Engines.Tests.Support;

namespace Insolvia.Engines.Tests;

// The ports against their oracle: the Python oracle's generator (deleted
// with the Python, 18.23; the outputs are committed) ran the Python
// projections and engines over four case files (the two reference cases of
// test_form_projections.py, and the latest seed fixture's two cases) and
// wrote every output; this suite parses the same case files through Core's
// parsers, runs the .NET ports, and diffs every value — every projected field
// of every form series, the means-test trace, the liens, the exemption
// analysis, the plan calculation, the IRS standards and the creditor matrix.
public sealed class ParityTests
{
    private static readonly string[] CaseFileNames = ["reference-ch7", "reference-ch13", "chapter-7-sample", "chapter-13-sample"];

    public static TheoryData<string, string> Outputs()
    {
        var data = new TheoryData<string, string>();
        foreach (var name in CaseFileNames)
        {
            var expected = CaseFiles.Expected(name);
            foreach (var (key, _) in expected)
            {
                if (key != "forms")
                {
                    data.Add(name, key);
                    continue;
                }

                foreach (var (series, _) in expected["forms"]!.AsObject())
                {
                    data.Add(name, $"forms/{series}");
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Outputs))]
    public void The_port_matches_the_python_oracle(string caseFile, string output)
    {
        var expected = CaseFiles.Expected(caseFile);
        var actual = Oracle.Outputs(CaseFiles.Named(caseFile));
        var (want, got) = output.StartsWith("forms/", StringComparison.Ordinal)
            ? (expected["forms"]![output["forms/".Length..]], actual["forms"]![output["forms/".Length..]])
            : (expected[output], actual[output]);

        var differences = Oracle.Diff(want, got, output);

        Assert.True(differences.Count == 0, string.Join("\n", differences.Take(40)));
    }
}
