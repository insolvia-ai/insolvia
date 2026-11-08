using System.Net.Sockets;
using Insolvia.Data.Connections;
using Npgsql;

namespace Insolvia.Data.Tests;

/// <summary>
/// The migrator's open through a waking Aurora: transient failures are
/// retried with a growing pause until the budget is spent; anything waiting
/// cannot fix is thrown at once.
/// </summary>
public sealed class DatabaseWakeTests
{
    // Nothing listens on port 1, so every open fails fast with a socket error
    // — the same shape as a paused cluster dropping the connection.
    private static NpgsqlDataSource Unreachable() =>
        NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=2");

    [Fact]
    public void Timeouts_and_socket_errors_are_transient_and_a_bad_login_is_not()
    {
        Assert.True(DatabaseWake.IsTransient(new SocketException((int)SocketError.TimedOut)));
        Assert.True(DatabaseWake.IsTransient(new TimeoutException()));
        Assert.True(DatabaseWake.IsTransient(new NpgsqlException("connect failed", new SocketException((int)SocketError.TimedOut))));
        Assert.False(DatabaseWake.IsTransient(new InvalidOperationException("password authentication failed")));
    }

    [Fact]
    public async Task An_unreachable_database_is_retried_with_a_growing_pause_until_the_budget_is_spent()
    {
        await using var dataSource = Unreachable();
        var pauses = new List<TimeSpan>();

        await Assert.ThrowsAnyAsync<Exception>(() => DatabaseWake.OpenAsync(
            dataSource,
            TestContext.Current.CancellationToken,
            budget: TimeSpan.FromSeconds(40),
            delay: (pause, _) =>
            {
                pauses.Add(pause);
                return Task.CompletedTask;
            }));

        Assert.NotEmpty(pauses);
        Assert.Equal(TimeSpan.FromSeconds(2), pauses[0]);
        Assert.All(pauses.Zip(pauses.Skip(1)), pair => Assert.True(pair.Second >= pair.First));
        Assert.All(pauses, pause => Assert.True(pause <= TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public async Task With_no_budget_the_first_failure_is_thrown_without_waiting()
    {
        await using var dataSource = Unreachable();
        var waited = false;

        await Assert.ThrowsAnyAsync<Exception>(() => DatabaseWake.OpenAsync(
            dataSource,
            TestContext.Current.CancellationToken,
            budget: TimeSpan.Zero,
            delay: (_, _) =>
            {
                waited = true;
                return Task.CompletedTask;
            }));

        Assert.False(waited);
    }
}
