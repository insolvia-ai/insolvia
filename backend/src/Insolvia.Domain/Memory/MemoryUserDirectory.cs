using Insolvia.Core.Ports;

namespace Insolvia.Core.Memory;

/// <summary>
/// The in-memory <see cref="IUserDirectory"/>. Mints a uuid where Cognito
/// would mint a subject, and — the part that matters — refuses a duplicate
/// address CASE-INSENSITIVELY, as the real pool does. It sends no invitation:
/// there is no mail here.
/// </summary>
public sealed class MemoryUserDirectory : IUserDirectory
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _subjects = new(StringComparer.Ordinal);
    private readonly HashSet<string> _confirmed = new(StringComparer.Ordinal);
    private readonly List<string> _resent = [];

    /// <summary>Address to subject, for every account created — a test affordance.</summary>
    public IReadOnlyDictionary<string, string> Subjects
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, string>(_subjects, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Every address an invitation was re-sent to, in order — a test affordance.</summary>
    public IReadOnlyList<string> Resent
    {
        get
        {
            lock (_gate)
            {
                return [.. _resent];
            }
        }
    }

    /// <summary>Put an account past first sign-in, so a resend is refused — a test affordance.</summary>
    public void Confirm(string email)
    {
        lock (_gate)
        {
            _confirmed.Add(email);
        }
    }

    /// <inheritdoc />
    public Task<string> CreateUserAsync(string email, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        lock (_gate)
        {
            if (_subjects.Keys.Any(existing => string.Equals(existing, email, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ConflictException("that email address already has an Insolvia account");
            }

            var subject = Guid.NewGuid().ToString();
            _subjects[email] = subject;
            return Task.FromResult(subject);
        }
    }

    /// <inheritdoc />
    public Task ResendInviteAsync(string email, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_subjects.ContainsKey(email))
            {
                throw new NotFoundException("no account exists for that address");
            }

            if (_confirmed.Contains(email))
            {
                throw new ConflictException("that user has already completed first sign-in; the forgot-password flow is the way back in");
            }

            _resent.Add(email);
        }

        return Task.CompletedTask;
    }
}
