using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DbdPresence.Core.Game;
using DbdPresence.Core.Online;
using DbdPresence.Core.Roster;
using DbdPresence.Online.Account;
using DbdPresence.Online.Api;

namespace DbdPresence.Online.Roster;

/// <summary>How one call of the report client ended, reduced to what the reporter acts on.</summary>
public enum RosterReportStatus
{
    /// <summary>The call succeeded.</summary>
    Ok,

    /// <summary>Online features are not available (signed out, no current licence, switched off): nothing is kept.</summary>
    NotAllowed,

    /// <summary>The service could not be reached or failed; try again later.</summary>
    Offline,

    /// <summary>The service asked the app to wait.</summary>
    RateLimited,

    /// <summary>This account is not the owner's game account (403 <c>not_roster_reporter</c>): nothing is kept or sent again.</summary>
    NotReporter,

    /// <summary>The service refused the report itself (400): that batch is dropped.</summary>
    Refused,
}

/// <summary>An answer of the report client.</summary>
/// <param name="Status">How the call ended.</param>
/// <param name="IsReporter">For the reporter check: whether this account reports.</param>
/// <param name="RetryAfter">The wait after a 429, or null.</param>
public sealed record RosterReportAnswer(RosterReportStatus Status, bool IsReporter = false, TimeSpan? RetryAfter = null);

/// <summary>The two service calls of the reporter (a seam for tests; the real one goes through the account's authorised calls).</summary>
public interface IRosterReportClient
{
    /// <summary>Asks the service whether this account is the owner's game account (<c>GET /v1/me</c>, <c>user.rosterReporter</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The answer.</returns>
    Task<RosterReportAnswer> CheckReporterAsync(CancellationToken cancellationToken);

    /// <summary>Sends one batch (<c>POST /v1/roster/sightings</c>).</summary>
    /// <param name="sightings">At most <see cref="CharacterSightingReport.MaxPerReport"/> sightings.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The answer.</returns>
    Task<RosterReportAnswer> SendAsync(IReadOnlyList<CharacterSightingReport> sightings, CancellationToken cancellationToken);
}

/// <summary>The real client: both calls need a current licence and go only to the compiled-in API host (ADR-0016).</summary>
public sealed partial class AccountRosterReportClient : IRosterReportClient
{
    private readonly AccountManager _account;
    private readonly ServiceClient _api;
    private readonly string _appVersion;

    /// <summary>Initializes a new instance of the <see cref="AccountRosterReportClient"/> class.</summary>
    /// <param name="account">The account (session, licence, revocation handling).</param>
    /// <param name="api">The service client.</param>
    /// <param name="appVersion">The app version, <c>major.minor.patch</c>.</param>
    public AccountRosterReportClient(AccountManager account, ServiceClient api, string appVersion)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(api);
        if (!VersionPattern().IsMatch(appVersion ?? string.Empty))
        {
            throw new ArgumentException("The app version is major.minor.patch.", nameof(appVersion));
        }

        _account = account;
        _api = api;
        _appVersion = appVersion!;
    }

    /// <summary>The version this build reports: the assembly's informational version without its build metadata, or 0.0.0.</summary>
    /// <returns>The version.</returns>
    public static string BuildVersion()
    {
        var text = typeof(AccountRosterReportClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
        var plus = text.IndexOf('+', StringComparison.Ordinal);
        var core = plus >= 0 ? text[..plus] : text;
        return VersionPattern().IsMatch(core) ? core : "0.0.0";
    }

    /// <inheritdoc />
    public async Task<RosterReportAnswer> CheckReporterAsync(CancellationToken cancellationToken)
    {
        var (result, failure) = await _account.CallAuthorizedAsync((token, ct) => _api.GetRosterReporterAsync(token, ct), cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            return new RosterReportAnswer(failure.Failure == AccountFailure.Offline ? RosterReportStatus.Offline : RosterReportStatus.NotAllowed);
        }

        return result!.Outcome switch
        {
            ApiOutcome.Ok => new RosterReportAnswer(RosterReportStatus.Ok, result.Value),
            ApiOutcome.RateLimited => new RosterReportAnswer(RosterReportStatus.RateLimited, RetryAfter: result.RetryAfter),
            ApiOutcome.Unauthorized or ApiOutcome.Forbidden => new RosterReportAnswer(RosterReportStatus.NotAllowed),
            _ => new RosterReportAnswer(RosterReportStatus.Offline),
        };
    }

    /// <inheritdoc />
    public async Task<RosterReportAnswer> SendAsync(IReadOnlyList<CharacterSightingReport> sightings, CancellationToken cancellationToken)
    {
        var (result, failure) = await _account.CallAuthorizedAsync((token, ct) => _api.ReportSightingsAsync(token, _appVersion, sightings, ct), cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            return new RosterReportAnswer(failure.Failure == AccountFailure.Offline ? RosterReportStatus.Offline : RosterReportStatus.NotAllowed);
        }

        return result!.Outcome switch
        {
            ApiOutcome.Ok => new RosterReportAnswer(RosterReportStatus.Ok),
            ApiOutcome.RateLimited => new RosterReportAnswer(RosterReportStatus.RateLimited, RetryAfter: result.RetryAfter),
            ApiOutcome.Forbidden when result.ErrorCode == "not_roster_reporter" => new RosterReportAnswer(RosterReportStatus.NotReporter),
            ApiOutcome.Unauthorized or ApiOutcome.Forbidden => new RosterReportAnswer(RosterReportStatus.NotAllowed),
            ApiOutcome.Rejected => new RosterReportAnswer(RosterReportStatus.Refused),
            _ => new RosterReportAnswer(RosterReportStatus.Offline),
        };
    }

    [GeneratedRegex(@"^\d{1,5}\.\d{1,5}\.\d{1,5}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}

/// <summary>
/// Reports character names that detection read on screen but could not find in the vocabulary (owner decision D35), implementing the
/// Core seam <see cref="IUnknownCharacterSink"/>. Only from the owner's own app: before anything is sent the service is asked whether this
/// account is the one the owner marked, and for every other account (friends' apps) the names are dropped and nothing is ever sent.
/// What leaves the PC is the folded name (<see cref="RosterNameFold"/>), the role, a random id per match (a fresh 128-bit value; the
/// caller's match key stays on the PC) and the app version: no player names, no images, no screen text beyond the name itself.
/// Sightings wait in memory only (never on disk), at most 32, de-duplicated per name, role and match, and go out in batches of at most
/// 4, at most once every 30 minutes, only while online features are allowed. Signing out forgets them.
/// </summary>
public sealed class UnknownCharacterReporter : IUnknownCharacterSink, IDisposable
{
    /// <summary>The wait before the first report, so a burst of sightings goes out together.</summary>
    public static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);

    /// <summary>The shortest time between two reports (the service allows 30 a day).</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(30);

    /// <summary>How long the answer "this is (not) the owner's account" is trusted before it is asked again.</summary>
    public static readonly TimeSpan ReporterRecheck = TimeSpan.FromHours(6);

    /// <summary>Most sightings waiting at once; more are dropped until some are sent.</summary>
    public const int MaxQueued = 32;

    private const int MaxMatches = 16;
    private const int MaxRemembered = 256;

    private readonly IRosterReportClient _client;
    private readonly IAccountService _account;
    private readonly TimeProvider _time;
    private readonly IOnlineLog _log;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<CharacterSightingReport> _queue = [];
    private readonly LinkedList<(long Key, string Id)> _matches = new();
    private readonly HashSet<string> _remembered = new(StringComparer.Ordinal);
    private readonly Queue<string> _rememberedOrder = new();
    private bool? _isReporter;
    private DateTimeOffset _checkedAt;
    private DateTimeOffset _notBefore;
    private ITimer? _timer;
    private Task _lastFlush = Task.CompletedTask;
    private bool _warnedFull;

    /// <summary>Initializes a new instance of the <see cref="UnknownCharacterReporter"/> class.</summary>
    /// <param name="client">The service calls.</param>
    /// <param name="account">The account; signing out forgets every waiting sighting.</param>
    /// <param name="time">The clock; timers come from it so tests can advance time.</param>
    /// <param name="log">Diagnostics.</param>
    public UnknownCharacterReporter(IRosterReportClient client, IAccountService account, TimeProvider time, IOnlineLog log)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        _client = client;
        _account = account;
        _time = time;
        _log = log;
        _notBefore = time.GetUtcNow() + FirstDelay;
        _account.Changed += OnAccountChanged;
    }

    /// <summary>Gets the sightings waiting to be sent (for tests and diagnostics).</summary>
    public int Pending
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count;
            }
        }
    }

    /// <summary>Gets the task of the latest send attempt (completed when none runs); tests await it.</summary>
    public Task LastFlush
    {
        get
        {
            lock (_gate)
            {
                return _lastFlush;
            }
        }
    }

    /// <inheritdoc />
    public void Report(UnknownCharacterSighting sighting)
    {
        ArgumentNullException.ThrowIfNull(sighting);
        var role = sighting.Role switch { Role.Killer => "killer", Role.Survivor => "survivor", _ => null };
        if (role is null || RosterNameFold.FoldForReport(sighting.Name) is not { } folded)
        {
            return;
        }

        lock (_gate)
        {
            if (_isReporter == false || _stop.IsCancellationRequested)
            {
                return;
            }

            var report = new CharacterSightingReport(folded, role, MatchIdFor(sighting.MatchKey));
            var key = $"{report.FoldedName}|{report.Role}|{report.MatchId}";
            if (_remembered.Contains(key))
            {
                return;
            }

            if (_queue.Count >= MaxQueued)
            {
                if (!_warnedFull)
                {
                    _warnedFull = true;
                    _log.Warning("Unknown character names are waiting to be reported; further ones are dropped until those are sent.");
                }

                return;
            }

            Remember(key);
            _queue.Add(report);
            ScheduleLocked();
        }
    }

    /// <summary>
    /// Takes the answer of the normal account refresh (<c>GET /v1/me</c>, <c>user.rosterReporter</c>): for any other account, waiting names are
    /// dropped and later ones are not queued at all, without a call of its own.
    /// </summary>
    /// <param name="isReporter">Whether this account is the owner's game account.</param>
    public void NoteReporter(bool isReporter)
    {
        lock (_gate)
        {
            _isReporter = isReporter;
            _checkedAt = _time.GetUtcNow();
            if (!isReporter)
            {
                _queue.Clear();
            }
        }
    }

    /// <summary>Stops the timer and forgets every waiting sighting.</summary>
    public void Dispose()
    {
        _account.Changed -= OnAccountChanged;
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _queue.Clear();
        }

        _stop.Cancel();
        _stop.Dispose();
    }

    private string MatchIdFor(long matchKey)
    {
        foreach (var (key, id) in _matches)
        {
            if (key == matchKey)
            {
                return id;
            }
        }

        var fresh = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        _matches.AddFirst((matchKey, fresh));
        if (_matches.Count > MaxMatches)
        {
            _matches.RemoveLast();
        }

        return fresh;
    }

    private void Remember(string key)
    {
        _remembered.Add(key);
        _rememberedOrder.Enqueue(key);
        if (_rememberedOrder.Count > MaxRemembered)
        {
            _remembered.Remove(_rememberedOrder.Dequeue());
        }
    }

    private void ScheduleLocked()
    {
        if (_timer is not null || _queue.Count == 0 || _stop.IsCancellationRequested)
        {
            return;
        }

        var due = _notBefore - _time.GetUtcNow();
        _timer = _time.CreateTimer(_ => Trigger(), null, due > TimeSpan.Zero ? due : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    private void Trigger()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            if (_stop.IsCancellationRequested)
            {
                return;
            }

            _lastFlush = FlushAsync();
        }
    }

    private async Task FlushAsync()
    {
        try
        {
            await FlushOnceAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log.Warning("Reporting unknown character names stopped because the app is closing.");
        }
        catch (ObjectDisposedException)
        {
            _log.Warning("Reporting unknown character names stopped because the app is closing.");
        }
    }

    private async Task FlushOnceAsync(CancellationToken cancellationToken)
    {
        bool needCheck;
        lock (_gate)
        {
            if (_queue.Count == 0)
            {
                return;
            }

            needCheck = _isReporter is null || _time.GetUtcNow() - _checkedAt >= ReporterRecheck;
        }

        if (needCheck)
        {
            var check = await _client.CheckReporterAsync(cancellationToken).ConfigureAwait(false);
            if (!Handle(check, sent: null))
            {
                return;
            }

            lock (_gate)
            {
                _isReporter = check.IsReporter;
                _checkedAt = _time.GetUtcNow();
                if (!check.IsReporter)
                {
                    // Not the owner's account (a friend's app): drop everything and never send.
                    _queue.Clear();
                    return;
                }
            }
        }

        List<CharacterSightingReport> batch;
        lock (_gate)
        {
            batch = [.. _queue.Take(CharacterSightingReport.MaxPerReport)];
        }

        var answer = await _client.SendAsync(batch, cancellationToken).ConfigureAwait(false);
        Handle(answer, batch);
    }

    /// <summary>Applies an answer to the queue and the schedule; true when the flow may go on.</summary>
    private bool Handle(RosterReportAnswer answer, List<CharacterSightingReport>? sent)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            switch (answer.Status)
            {
                case RosterReportStatus.Ok:
                    if (sent is null)
                    {
                        return true;
                    }

                    _queue.RemoveAll(sent.Contains);
                    _warnedFull = false;
                    _notBefore = now + MinInterval;
                    break;
                case RosterReportStatus.NotReporter:
                    _isReporter = false;
                    _checkedAt = now;
                    _queue.Clear();
                    return false;
                case RosterReportStatus.NotAllowed:
                    // Only while online features are allowed: nothing is kept for later, and the pace still holds.
                    _queue.Clear();
                    _notBefore = now + MinInterval;
                    return false;
                case RosterReportStatus.Refused:
                    _log.Warning("The service refused a report of unknown character names; it was dropped.");
                    if (sent is not null)
                    {
                        _queue.RemoveAll(sent.Contains);
                    }

                    _notBefore = now + MinInterval;
                    break;
                case RosterReportStatus.RateLimited:
                    _notBefore = now + (answer.RetryAfter is { } wait && wait > MinInterval ? wait : MinInterval);
                    break;
                default:
                    _notBefore = now + MinInterval;
                    break;
            }

            ScheduleLocked();
            return answer.Status == RosterReportStatus.Ok;
        }
    }

    private void OnAccountChanged(object? sender, EventArgs e)
    {
        if (_account.Current.IsSignedIn)
        {
            return;
        }

        lock (_gate)
        {
            _queue.Clear();
            _matches.Clear();
            _isReporter = null;
        }
    }
}
