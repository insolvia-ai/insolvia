namespace Insolvia.Api.Tests.OpenApi;

public sealed class DumpTests
{
    [Fact]
    public async Task Dump()
    {
        using var world = new Support.FirmWorld();
        using var client = world.Host.CreateClient();
        var text = await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Environment.GetEnvironmentVariable("DUMP_TO")!, text, TestContext.Current.CancellationToken);
    }
}
