using System.Text.RegularExpressions;

namespace DbdPresence.LocalCi;

/// <summary>
/// Removes secrets from any text before it is logged or shown: registered secrets, and anything shaped like a GitHub token. Only the secrets that are current are kept (local CI round 3, red-team L10):
/// a secret registered for one job (<see cref="RegisterScoped"/>, the runner configuration) is dropped again when the job's scope ends, so the list does not grow with every job for the life of the loop.
/// </summary>
public sealed partial class Redactor
{
    public const string Hidden = "[hidden]";

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    // The secrets, longest first, replaced as a whole whenever the set changes; a reader takes the array it sees and never a half-built one.
    private string[] _ordered = [];

    /// <summary>How many secrets are registered right now (permanent ones and the ones of scopes still open).</summary>
    public int SecretCount
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Registers a secret for as long as the program runs (the token, the account's password).</summary>
    public void Register(SecretText secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        Register(secret.Reveal());
    }

    /// <summary>Registers a secret for as long as the program runs.</summary>
    public void Register(string secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return;
        }

        lock (_gate)
        {
            EntryFor(secret).Permanent = true;
        }
    }

    /// <summary>
    /// Registers a secret that is only wanted for a while (one job's runner configuration). It is removed again when the returned object is disposed, unless it was also registered for good in the
    /// meantime. Two scopes for the same value keep it until both have ended.
    /// </summary>
    public IDisposable RegisterScoped(SecretText secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var value = secret.Reveal();
        lock (_gate)
        {
            EntryFor(value).Holders++;
        }

        return new Scope(this, value);
    }

    public string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var result = text;
        foreach (var secret in Volatile.Read(ref _ordered))
        {
            result = result.Replace(secret, Hidden, StringComparison.Ordinal);
        }

        return TokenShape().Replace(result, Hidden);
    }

    /// <summary>An exception as one safe line: its type and message, without secrets.</summary>
    public string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Redact($"{exception.GetType().Name}: {exception.Message}");
    }

    // Called with the lock held.
    private Entry EntryFor(string value)
    {
        if (!_entries.TryGetValue(value, out var entry))
        {
            entry = new Entry();
            _entries[value] = entry;
            Publish();
        }

        return entry;
    }

    private void Release(string value)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(value, out var entry))
            {
                return;
            }

            entry.Holders--;
            if (entry.Holders <= 0 && !entry.Permanent)
            {
                _entries.Remove(value);
                Publish();
            }
        }
    }

    // Called with the lock held.
    private void Publish() => Volatile.Write(ref _ordered, [.. _entries.Keys.OrderByDescending(k => k.Length).ThenBy(k => k, StringComparer.Ordinal)]);

    [GeneratedRegex(@"\b(?:github_pat_|ghp_|gho_|ghu_|ghs_|ghr_)[A-Za-z0-9_]{16,}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenShape();

    private sealed class Entry
    {
        public bool Permanent { get; set; }

        public int Holders { get; set; }
    }

    private sealed class Scope : IDisposable
    {
        private readonly Redactor _owner;
        private readonly string _value;
        private int _disposed;

        public Scope(Redactor owner, string value)
        {
            _owner = owner;
            _value = value;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Release(_value);
            }
        }
    }
}
