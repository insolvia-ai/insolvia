using Insolvia.Data.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Insolvia.Data.Waitlist;

// The marketing site's waitlist (services/api core/waitlist.py and its two
// adapters). The port, the record and the memory twin live HERE rather than in
// Insolvia.Core only because Core was closed to this change (#525, while the
// case domain landed there): they move to Core.Ports / Core.Memory unchanged
// when it opens, and nothing outside this file names their namespace but the
// API's composition and its tests.

/// <summary>
/// A validated, trimmed submission. The optional fields are <c>""</c> when
/// absent — Python's <c>WaitlistSubmission</c>.
/// </summary>
/// <param name="Name">Who.</param>
/// <param name="Firm">Their firm.</param>
/// <param name="Email">A work address.</param>
/// <param name="CurrentSoftware">What they use today, or <c>""</c>.</param>
/// <param name="Message">Anything else, or <c>""</c>.</param>
/// <param name="Host">The marketing host that served the form, or <c>""</c>.</param>
public sealed record WaitlistSubmission(string Name, string Firm, string Email, string CurrentSoftware, string Message, string Host);

/// <summary>A submission plus the server-generated identity (Python's <c>WaitlistRecord</c>).</summary>
/// <param name="Id">A uuid4, minted by the API.</param>
/// <param name="SubmittedAt">When, UTC, to the millisecond.</param>
/// <param name="Submission">What was submitted.</param>
public sealed record WaitlistRecord(string Id, DateTimeOffset SubmittedAt, WaitlistSubmission Submission);

/// <summary>
/// Persists waitlist submissions (Python's <c>WaitlistStore</c>). Write-only:
/// the API never reads the list back, so the API's database role holds
/// <c>INSERT</c> on the table and nothing else.
/// </summary>
public interface IWaitlistStore
{
    /// <summary>
    /// Store <paramref name="record"/>. MUST refuse an id already stored —
    /// stricter than the DynamoDB put it replaces, which overwrote; ids are
    /// uuid4s, so the refusal can only catch a bug.
    /// </summary>
    /// <param name="record">The record.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the record is stored.</returns>
    Task AddAsync(WaitlistRecord record, CancellationToken cancellationToken = default);
}

/// <summary>
/// The in-memory <see cref="IWaitlistStore"/>: tests, and a developer's
/// <c>dotnet run</c>. Never composed in a deployed environment.
/// </summary>
public sealed class MemoryWaitlistStore : IWaitlistStore
{
    private readonly Lock _gate = new();
    private readonly List<WaitlistRecord> _records = [];

    /// <summary>Every record stored, in order — a test affordance.</summary>
    public IReadOnlyList<WaitlistRecord> Records
    {
        get
        {
            lock (_gate)
            {
                return [.. _records];
            }
        }
    }

    /// <inheritdoc />
    public Task AddAsync(WaitlistRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            if (_records.Any(stored => stored.Id == record.Id))
            {
                throw new InvalidOperationException($"waitlist record {record.Id} is already stored");
            }

            _records.Add(record);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// One waitlist submission (Python's <c>record_item</c>, minus the DynamoDB
/// keys). The optional fields are NULL when the submission left them empty —
/// the item omitted them rather than storing <c>""</c>, and so does this row.
/// </summary>
internal sealed class WaitlistEntryRow
{
    public const string Table = "waitlist_entries";
    public const string PrimaryKey = "pk_waitlist_entries";

    /// <summary>The status every submission starts at; nothing writes another yet.</summary>
    public const string NewStatus = "new";

    public string Id { get; set; } = "";

    public DateTimeOffset SubmittedAt { get; set; }

    public string Status { get; set; } = NewStatus;

    public string Name { get; set; } = "";

    public string Firm { get; set; } = "";

    public string Email { get; set; } = "";

    public string? Host { get; set; }

    public string? CurrentSoftware { get; set; }

    public string? Message { get; set; }

    public static WaitlistEntryRow From(WaitlistRecord record) => new()
    {
        Id = record.Id,
        SubmittedAt = record.SubmittedAt,
        Status = NewStatus,
        Name = record.Submission.Name,
        Firm = record.Submission.Firm,
        Email = record.Submission.Email,
        Host = NullIfEmpty(record.Submission.Host),
        CurrentSoftware = NullIfEmpty(record.Submission.CurrentSoftware),
        Message = NullIfEmpty(record.Submission.Message),
    };

    public WaitlistRecord ToDomain() =>
        new(Id, SubmittedAt, new WaitlistSubmission(Name, Firm, Email, CurrentSoftware ?? "", Message ?? "", Host ?? ""));

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}

internal sealed class WaitlistEntryRowConfiguration : IEntityTypeConfiguration<WaitlistEntryRow>
{
    public void Configure(EntityTypeBuilder<WaitlistEntryRow> builder)
    {
        builder.ToTable(WaitlistEntryRow.Table, table => table.HasCheckConstraint("ck_waitlist_entries_status", "status IN ('new')"));
        builder.HasKey(entry => entry.Id).HasName(WaitlistEntryRow.PrimaryKey);

        // The DynamoDB table's one query: the whole list, oldest first
        // (SK = "<submittedAt>#<id>").
        builder.HasIndex(entry => new { entry.SubmittedAt, entry.Id });
    }
}

/// <summary>
/// The Postgres <see cref="IWaitlistStore"/> (Python's
/// <c>DynamoDbWaitlistStore</c>): one <c>INSERT</c>, the only statement it
/// makes and the only privilege the API's role holds on the table.
/// </summary>
public sealed class PostgresWaitlistStore : IWaitlistStore
{
    private readonly InsolviaDbContext _db;

    /// <summary>A store over <paramref name="db"/> (request-scoped).</summary>
    /// <param name="db">The context.</param>
    public PostgresWaitlistStore(InsolviaDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <inheritdoc />
    public Task AddAsync(WaitlistRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        return _db.InsertOnceAsync(
            WaitlistEntryRow.From(record),
            WaitlistEntryRow.PrimaryKey,
            () => new InvalidOperationException($"waitlist record {record.Id} is already stored"),
            cancellationToken);
    }
}
