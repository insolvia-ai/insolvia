using System.Net.Sockets;
using Npgsql;

namespace Insolvia.Data.Connections;

/// <summary>
/// Opens a connection to a database that may be waking up. Aurora Serverless
/// v2 at a minimum capacity of 0 pauses after its idle window; the first
/// connection after that resumes it, and until it has resumed a TCP attempt
/// is dropped and times out. The staging migrator's first run on the switch
/// failed exactly that way (2026-10-30, "Failed to connect … Connection timed
/// out" while capacity went 0 → 1), so a one-shot caller that runs right after
/// an idle stretch — the migrator, the purge — opens through this.
/// </summary>
public static class DatabaseWake
{
    /// <summary>How long to keep trying before the failure is real.</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Opens a connection, retrying a transient failure (a timeout or a
    /// socket error) with a growing pause until <paramref name="budget"/> is
    /// spent. Anything else — a refused login, a bad database name — is thrown
    /// at once, because waiting cannot fix it.
    /// </summary>
    /// <param name="dataSource">The data source to open from.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <param name="budget">Overrides <see cref="DefaultBudget"/>.</param>
    /// <param name="delay">A test seam for the pause between attempts.</param>
    /// <returns>An open connection; the caller disposes it.</returns>
    public static async Task<NpgsqlConnection> OpenAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken,
        TimeSpan? budget = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        var deadline = DateTimeOffset.UtcNow + (budget ?? DefaultBudget);
        var pause = TimeSpan.FromSeconds(2);
        delay ??= Task.Delay;

        while (true)
        {
            try
            {
                return await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (IsTransient(error) && DateTimeOffset.UtcNow + pause < deadline)
            {
                await delay(pause, cancellationToken).ConfigureAwait(false);
                pause = TimeSpan.FromSeconds(Math.Min(pause.TotalSeconds * 2, 15));
            }
        }
    }

    /// <summary>True when waiting could fix <paramref name="error"/>.</summary>
    public static bool IsTransient(Exception error) => error switch
    {
        NpgsqlException { IsTransient: true } => true,
        NpgsqlException { InnerException: SocketException or TimeoutException } => true,
        SocketException or TimeoutException => true,
        _ => false,
    };
}
