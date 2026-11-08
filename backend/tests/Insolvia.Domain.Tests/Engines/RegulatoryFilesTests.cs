using Insolvia.Domain.Engines.Regulatory;

namespace Insolvia.Domain.Engines.Tests;

// The regulatory files this assembly embeds (ADR 0014: they ship in the
// artifact). Since 18.23 deleted the Python registry they were copied from,
// backend/src/Insolvia.Domain/Engines/Regulatory IS the registry: a release is a
// reviewed PR to that directory (src/Insolvia.UstTool writes the UST
// series there). The template PDFs and the filing/ series ship with
// Insolvia.Domain.Forms; the court registry with Insolvia.Domain.
public sealed class RegulatoryFilesTests
{
    [Fact]
    public void The_embedded_tree_holds_every_series_the_engines_read()
    {
        var series = RegistryTree.Shipped.ChildDirectories("code")
            .Select(name => $"code/{name}")
            .Concat(RegistryTree.Shipped.ChildDirectories("ust").Select(name => $"ust/{name}"))
            .Concat(RegistryTree.Shipped.ChildDirectories("exemptions").Select(name => $"exemptions/{name}"))
            .ToList();

        Assert.Equal(
            ["code/deadline-rules", "code/dollar-amounts", "code/legal-holidays", "ust/census-median-family-income", "ust/ch13-admin-multipliers", "ust/irs-local-standards", "ust/irs-national-standards", "exemptions/federal", "exemptions/fl", "exemptions/ga", "exemptions/tx"],
            series);
        Assert.Equal(22, RegistryTree.Shipped.ChildDirectories("form").Count);
    }
}
