using Insolvia.Data.Pagination;

namespace Insolvia.Data.Tests;

public sealed record WidgetKey(int SortKey, Guid Id);

[Collection(PostgresTests.Name)]
public sealed class PaginationTests(PostgresFixture postgres)
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static readonly CursorCodec Codec = new(new byte[32]);

    private static Keyset<Widget, WidgetKey> Listing(string name) => new(
        name,
        q => q.OrderByDescending(w => w.SortKey).ThenByDescending(w => w.Id),
        w => new WidgetKey(w.SortKey, w.Id),
        (q, k) => q.Where(w => w.SortKey < k.SortKey || (w.SortKey == k.SortKey && w.Id.CompareTo(k.Id) < 0)));

    [Fact]
    public void A_cursor_round_trips_through_the_query_that_minted_it()
    {
        var key = new WidgetKey(7, Guid.NewGuid());

        var cursor = Codec.Encode("cases.by-firm", key);

        Assert.Equal(key, Codec.Decode<WidgetKey>("cases.by-firm", cursor));
        Assert.DoesNotContain("SortKey", cursor, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cursor_from_one_listing_is_refused_by_another()
    {
        var cursor = Codec.Encode("cases.by-firm", new WidgetKey(7, Guid.NewGuid()));

        Assert.Throws<InvalidCursorException>(() => Codec.Decode<WidgetKey>("cases.by-firm:archived", cursor));
        Assert.Throws<InvalidCursorException>(() => Codec.Decode<WidgetKey>("cases.by-assignee", cursor));
    }

    [Fact]
    public void An_edited_cursor_is_refused()
    {
        var cursor = Codec.Encode("cases.by-firm", new WidgetKey(7, Guid.Empty));
        var payload = System.Text.Encoding.UTF8.GetBytes("""{"SortKey":999,"Id":"00000000-0000-0000-0000-000000000000"}""");
        var forged = Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_') + cursor[cursor.IndexOf('.', StringComparison.Ordinal)..];

        Assert.Throws<InvalidCursorException>(() => Codec.Decode<WidgetKey>("cases.by-firm", forged));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-cursor")]
    [InlineData(".")]
    [InlineData("eyJ9.AAAA")]
    [InlineData("!!!.???")]
    public void A_malformed_cursor_is_refused_with_one_message(string cursor)
    {
        var error = Assert.Throws<InvalidCursorException>(() => Codec.Decode<WidgetKey>("cases.by-firm", cursor));

        Assert.Equal("cursor is not valid", error.Message);
    }

    [Fact]
    public void A_cursor_minted_under_another_key_is_refused()
    {
        var other = new CursorCodec(Enumerable.Repeat((byte)1, 32).ToArray());
        var cursor = other.Encode("cases.by-firm", new WidgetKey(1, Guid.Empty));

        Assert.Throws<InvalidCursorException>(() => Codec.Decode<WidgetKey>("cases.by-firm", cursor));
    }

    [Fact]
    public void The_key_comes_from_the_environment_when_one_is_configured()
    {
        var key = Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray());
        var configured = CursorCodec.FromEnvironment(name => name == CursorCodec.KeyVariable ? key : null);
        var fallback = CursorCodec.FromEnvironment(_ => null);

        var cursor = configured.Encode("q", new WidgetKey(1, Guid.Empty));

        Assert.Equal(new WidgetKey(1, Guid.Empty), configured.Decode<WidgetKey>("q", cursor));
        Assert.Throws<InvalidCursorException>(() => fallback.Decode<WidgetKey>("q", cursor));
    }

    [Fact]
    public async Task Paging_reads_every_row_exactly_once_in_order_even_across_ties_on_the_sort_key()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var context = await TestDbContext.CreateAsync(connectionString);
        var firm = Guid.NewGuid();
        // 25 rows over 5 sort values: every page boundary falls inside a tie.
        context.Widgets.AddRange(Enumerable.Range(0, 25).Select(i => new Widget { FirmId = firm, Id = Guid.NewGuid(), SortKey = i % 5 }));
        context.Widgets.Add(new Widget { FirmId = Guid.NewGuid(), Id = Guid.NewGuid(), SortKey = 3 });
        await context.SaveChangesAsync(None);
        var listing = Listing("widgets.by-firm");
        var scoped = context.Widgets.Where(w => w.FirmId == firm);

        var seen = new List<Widget>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await scoped.ToKeysetPageAsync(listing, Codec, cursor, 10, None);
            seen.AddRange(page.Items);
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null);

        Assert.Equal(3, pages);
        var expected = scoped.AsEnumerable().OrderByDescending(w => w.SortKey).ThenByDescending(w => w.Id).Select(w => w.Id).ToList();
        Assert.Equal(expected, seen.Select(w => w.Id).ToList());
    }

    [Fact]
    public async Task The_last_page_has_no_next_cursor_even_when_it_is_exactly_full()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var context = await TestDbContext.CreateAsync(connectionString);
        context.Widgets.AddRange(Enumerable.Range(0, 10).Select(i => new Widget { FirmId = Guid.Empty, Id = Guid.NewGuid(), SortKey = i }));
        await context.SaveChangesAsync(None);

        var page = await context.Widgets.ToKeysetPageAsync(Listing("widgets"), Codec, null, 10, None);

        Assert.Equal(10, page.Items.Count);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task A_listing_refuses_a_cursor_another_listing_minted()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var context = await TestDbContext.CreateAsync(connectionString);
        context.Widgets.AddRange(Enumerable.Range(0, 3).Select(i => new Widget { FirmId = Guid.Empty, Id = Guid.NewGuid(), SortKey = i }));
        await context.SaveChangesAsync(None);
        var working = await context.Widgets.ToKeysetPageAsync(Listing("widgets"), Codec, null, 1, None);

        await Assert.ThrowsAsync<InvalidCursorException>(() =>
            context.Widgets.ToKeysetPageAsync(Listing("widgets:archived"), Codec, working.NextCursor, 1, None));
    }
}
