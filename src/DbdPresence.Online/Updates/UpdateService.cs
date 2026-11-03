using System.Text;
using DbdPresence.Core.Online;
using DbdPresence.Online.Api;
using DbdPresence.Online.Packs;
using DbdPresence.Packs.Releases;
using DbdPresence.Packs.Signing;

namespace DbdPresence.Online.Updates;

/// <summary>How an update check ended.</summary>
public enum UpdateCheckOutcome
{
    /// <summary>A valid feed was read and it offers nothing newer than this version (a lower version is never offered: no downgrade).</summary>
    UpToDate,

    /// <summary>A valid feed offers a newer version (<see cref="UpdateCheck.Candidate"/>).</summary>
    UpdateAvailable,

    /// <summary>Online features are off (signed out, no current licence, or the emergency switch): nothing was contacted.</summary>
    OnlineFeaturesOff,

    /// <summary>No usable answer (offline, timeout, server error).</summary>
    Unreachable,

    /// <summary>The service asked the app to wait (429).</summary>
    RateLimited,

    /// <summary>Nothing is published on this channel.</summary>
    NothingPublished,

    /// <summary>The feed was refused: bad signature, wrong key, expired, rolled back or malformed (<see cref="UpdateCheck.Refusal"/>).</summary>
    FeedRefused,

    /// <summary>The stored version floor or the kept key replacement could not be read or saved, so no feed is used (fail closed; the log says why).</summary>
    FloorUnavailable,
}

/// <summary>The result of one update check.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Candidate">The package to install when <see cref="UpdateCheckOutcome.UpdateAvailable"/>; otherwise null.</param>
/// <param name="FeedVersion">The verified feed's version, or null.</param>
/// <param name="Refusal">Why the feed was refused, or null.</param>
/// <param name="Detail">A short reason for a refused payload (for the log), or null.</param>
/// <param name="CheckedAt">When the check ran.</param>
/// <param name="Kid">The key that signed the verified feed, or null.</param>
/// <param name="ExpiresAt">When the verified feed expires, or null.</param>
public sealed record UpdateCheck(
    UpdateCheckOutcome Outcome, ReleasePackage? Candidate, long? FeedVersion, VerificationOutcome? Refusal, string? Detail, DateTimeOffset CheckedAt, string? Kid = null, DateTimeOffset? ExpiresAt = null);

/// <summary>How a package download ended.</summary>
public enum UpdateDownloadOutcome
{
    /// <summary>The package is on disk with exactly the size and SHA-256 of the signed feed.</summary>
    Downloaded,

    /// <summary>Online features are off; nothing was contacted.</summary>
    OnlineFeaturesOff,

    /// <summary>No usable answer.</summary>
    Unreachable,

    /// <summary>The service asked the app to wait.</summary>
    RateLimited,

    /// <summary>The bytes did not match the signed feed (or the service refused the file); they were deleted.</summary>
    Refused,
}

/// <summary>What <see cref="UpdateService.Judge"/> decides about a downloaded package.</summary>
public enum PackageVerdict
{
    /// <summary>It may be installed.</summary>
    Valid,

    /// <summary>It cannot be vouched for right now (no feed verified in this run, the last check could not reach the service, the feed expired, or the kept state is damaged), but nothing is wrong with it: keep the file and do not install it.</summary>
    Unverifiable,

    /// <summary>It must be deleted: the verified feed is gone, no longer offers it, another package or a newer feed took its place, its key was revoked, or the file changed.</summary>
    Invalid,
}

/// <summary>
/// Signed updates (FR-ONL-10, PLAN_V2 §6.5, ADR-0012): reads the release feed of one channel, verifies it before parsing (release key only,
/// channel bound, not expired, not rolled back), picks the newest package that is higher than the running version (never a lower one), and
/// downloads exactly that package from the CDN host, accepting it only with the feed's size and SHA-256. This is the independent check that
/// keeps updates safe without Authenticode (D33): an unsigned installer is fine because every update package is verified against our own
/// signature. Velopack is not referenced here; the app adapts this service to Velopack's update source interface.
/// Rollback protection does not depend on downloads: as soon as a feed's signature and payload check out, its version raises a floor kept on
/// disk (<see cref="IReleaseFloor"/>), and any later feed below that floor is refused. Freeze protection is the feed's 30-day expiry.
/// </summary>
public sealed class UpdateService
{
    /// <summary>The channel the app reads.</summary>
    public const string StableChannel = "stable";

    private readonly IReleaseTransport _transport;
    private readonly SignedDocumentVerifier _verifier;
    private readonly IPackSyncGate _gate;
    private readonly KeyReplacementService? _keys;
    private readonly IReleaseFloor _floor;
    private readonly TimeProvider _time;
    private readonly IOnlineLog _log;
    private readonly Lock _lock = new();
    private UpdateCheck? _last;
    private UpdateCheck? _offer;

    /// <summary>Initializes a new instance of the <see cref="UpdateService"/> class.</summary>
    /// <param name="transport">Where the feed and packages come from.</param>
    /// <param name="verifier">The signed-document verifier; only a key trusted for <c>releases</c> can sign a feed.</param>
    /// <param name="gate">Online features gate (a current licence is needed).</param>
    /// <param name="floor">The highest verified feed version, kept on disk.</param>
    /// <param name="channel">The channel to read.</param>
    /// <param name="time">The clock.</param>
    /// <param name="log">Diagnostics.</param>
    /// <param name="keys">The key-replacement check run before each feed check, or null.</param>
    public UpdateService(IReleaseTransport transport, SignedDocumentVerifier verifier, IPackSyncGate gate, IReleaseFloor floor, string channel, TimeProvider time, IOnlineLog log, KeyReplacementService? keys = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(floor);
        _floor = floor;
        ArgumentException.ThrowIfNullOrEmpty(channel);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        _transport = transport;
        _verifier = verifier;
        _gate = gate;
        Channel = channel;
        _time = time;
        _log = log;
        _keys = keys;
    }

    /// <summary>Gets the channel this service reads.</summary>
    public string Channel { get; }

    /// <summary>Gets the most recent check, or null before the first one.</summary>
    public UpdateCheck? LastCheck
    {
        get
        {
            lock (_lock)
            {
                return _last;
            }
        }
    }

    /// <summary>Gets the most recent check that verified a feed and offered a package, or null before one did. A later outage does not clear it.</summary>
    public UpdateCheck? LastOffer
    {
        get
        {
            lock (_lock)
            {
                return _offer;
            }
        }
    }

    /// <summary>Reads and verifies the feed and picks the package to install, if any.</summary>
    /// <param name="current">The running app version.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The check.</returns>
    public async Task<UpdateCheck> CheckAsync(ReleaseVersion current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        var check = await RunCheckAsync(current, cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            _last = check;
            if (check.Outcome == UpdateCheckOutcome.UpdateAvailable)
            {
                _offer = check;
            }
        }

        return check;
    }

    /// <summary>
    /// Downloads the candidate of the last check to <paramref name="path"/>. Only a package that the last verified feed offered can be
    /// downloaded; on any failure the file is deleted. (The feed's version raised the rollback floor already when the feed was verified.)
    /// </summary>
    /// <param name="package">The package; must be <see cref="UpdateCheck.Candidate"/> of <see cref="LastCheck"/>.</param>
    /// <param name="path">The file to write.</param>
    /// <param name="progress">Progress from 0 to 100, or null.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>The outcome.</returns>
    /// <exception cref="InvalidOperationException">The package is not the one the last verified feed offered.</exception>
    public async Task<UpdateDownloadOutcome> DownloadAsync(ReleasePackage package, string path, Action<int>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrEmpty(path);
        var last = LastCheck;
        if (last is not { Outcome: UpdateCheckOutcome.UpdateAvailable, Candidate: { } candidate } || candidate != package)
        {
            throw new InvalidOperationException("Only the package offered by the last verified release feed can be downloaded.");
        }

        if (!_gate.IsOpen)
        {
            return UpdateDownloadOutcome.OnlineFeaturesOff;
        }

        ApiResult<bool> result;
        var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        await using (file.ConfigureAwait(false))
        {
            result = await _transport.DownloadAsync(package, file, progress, cancellationToken).ConfigureAwait(false);
        }

        if (result.IsOk && result.Value)
        {
            return UpdateDownloadOutcome.Downloaded;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The refused bytes stay in Velopack's partial file, which it deletes itself; they are never applied (no matching hash).
            _log.Warning("A refused update download could not be deleted: " + ex.GetType().Name);
        }

        _log.Warning($"Update package {package.Version} was not downloaded: {result.Outcome} {result.ErrorCode}".TrimEnd());
        return result.Outcome switch
        {
            ApiOutcome.Unreachable or ApiOutcome.ServerError => UpdateDownloadOutcome.Unreachable,
            ApiOutcome.RateLimited => UpdateDownloadOutcome.RateLimited,
            _ => UpdateDownloadOutcome.Refused,
        };
    }

    /// <summary>
    /// Checks a downloaded package again just before it is installed or kept (security review 2026-10-25, M3 and the outage finding). It is
    /// <see cref="PackageVerdict.Valid"/> only while the last package a verified feed offered is this one, that feed has not expired, its key is
    /// still trusted (a key replacement may have revoked it since), no newer feed was verified since, the kept state is intact and the file has
    /// exactly the signed size and SHA-256. It is <see cref="PackageVerdict.Invalid"/> when the newest check refused its feed or no longer
    /// offers it, another package was offered, the key was revoked, a newer feed came, or the file changed. A check that merely failed (offline,
    /// rate-limited, signed out) leaves the earlier offer standing, so a short outage does not cost a 95 MB download.
    /// </summary>
    /// <param name="package">The package as the last verified feed lists it.</param>
    /// <param name="path">The downloaded file.</param>
    /// <param name="reason">Why it is not valid, or null.</param>
    /// <returns>The verdict.</returns>
    public PackageVerdict Judge(ReleasePackage package, string path, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrEmpty(path);
        var last = LastCheck;
        var offer = LastOffer;
        PackageVerdict verdict;
        (verdict, reason) = last switch
        {
            { Outcome: UpdateCheckOutcome.FeedRefused or UpdateCheckOutcome.UpToDate } => (PackageVerdict.Invalid, "the newest verified feed does not offer it"),
            _ when offer is not { Candidate: not null, FeedVersion: not null, Kid: not null, ExpiresAt: not null } => (PackageVerdict.Unverifiable, "no feed offering it has been verified in this run"),
            _ when offer.Candidate != package => (PackageVerdict.Invalid, "the newest verified feed offers another package"),
            _ when !_verifier.Keys.IsAllowedFor(offer.Kid!, SignedPurposes.Releases) => (PackageVerdict.Invalid, "the key that signed its feed is no longer trusted"),
            _ when _floor.Highest(Channel) is { } highest && highest > offer.FeedVersion => (PackageVerdict.Invalid, "a newer feed was verified since"),
            _ when _time.GetUtcNow() >= offer.ExpiresAt => (PackageVerdict.Unverifiable, "the feed that offered it has expired"),
            _ when _keys is { IsDamaged: true } || _floor.IsDamaged => (PackageVerdict.Unverifiable, "the kept key replacement or version floor is damaged"),
            _ => (PackageVerdict.Valid, null),
        };
        if (verdict == PackageVerdict.Valid && !FileMatches(path, package))
        {
            verdict = PackageVerdict.Invalid;
            reason = "the file does not match the signed size and SHA-256";
        }

        if (reason is not null)
        {
            _log.Warning($"Downloaded update {package.Version} is not installed: {reason}.");
        }

        return verdict;
    }

    private static bool FileMatches(string path, ReleasePackage package)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != package.Size)
            {
                return false;
            }

            using var stream = File.OpenRead(path);
            return string.Equals(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream)), package.Sha256, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file that cannot be read cannot be shown to match: it is not installed.
            return false;
        }
    }

    private async Task<UpdateCheck> RunCheckAsync(ReleaseVersion current, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (!_gate.IsOpen)
        {
            return new UpdateCheck(UpdateCheckOutcome.OnlineFeaturesOff, null, null, null, null, now);
        }

        if (_keys is not null)
        {
            await _keys.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_keys is { IsDamaged: true })
        {
            // Fail closed (L2): the kept key replacement could not be applied, so a revoked key might be trusted. KeyReplacementService logged why.
            return new UpdateCheck(UpdateCheckOutcome.FloorUnavailable, null, null, null, "key_replacement_damaged", now);
        }

        // Floors set by a key that a key replacement removed no longer count (TM-01).
        _floor.Forget(_verifier.RevokedKeyIds);
        if (_floor.IsDamaged)
        {
            // Fail closed: without the floor an older, validly signed feed could not be told apart. FileReleaseFloor logged why.
            return new UpdateCheck(UpdateCheckOutcome.FloorUnavailable, null, null, null, "floor_damaged", now);
        }

        var answer = await _transport.GetFeedAsync(Channel, cancellationToken).ConfigureAwait(false);
        if (!answer.IsOk)
        {
            var outcome = answer switch
            {
                { Outcome: ApiOutcome.RateLimited } => UpdateCheckOutcome.RateLimited,
                { Outcome: ApiOutcome.Rejected, ErrorCode: "no_release" } => UpdateCheckOutcome.NothingPublished,
                _ => UpdateCheckOutcome.Unreachable,
            };
            return new UpdateCheck(outcome, null, null, null, answer.ErrorCode, now);
        }

        var verified = _verifier.Verify(Encoding.UTF8.GetBytes(answer.Value!), SignedPurposes.Releases, Channel);
        if (!verified.IsValid)
        {
            _log.Warning("Release feed refused: " + verified.Outcome);
            return new UpdateCheck(UpdateCheckOutcome.FeedRefused, null, null, verified.Outcome, null, now);
        }

        if (!ReleaseFeed.TryParse(verified.Envelope!.PayloadJson, Channel, out var feed, out var error))
        {
            _log.Warning("Release feed refused: payload " + error);
            return new UpdateCheck(UpdateCheckOutcome.FeedRefused, null, null, VerificationOutcome.Malformed, error, now);
        }

        // Rollback floor, independent of any download: a feed below the highest verified one is refused, and this one raises it now.
        var version = verified.Envelope.Version;
        if (_floor.Highest(Channel) is { } highest && version < highest)
        {
            _log.Warning($"Release feed refused: version {version} is lower than {highest}, already verified.");
            return new UpdateCheck(UpdateCheckOutcome.FeedRefused, null, null, VerificationOutcome.RolledBack, null, now);
        }

        if (!_floor.Raise(Channel, version, verified.Envelope.Kid))
        {
            return new UpdateCheck(UpdateCheckOutcome.FloorUnavailable, null, null, null, "floor_not_saved", now);
        }

        _verifier.CommitVersion(verified.Envelope);
        var candidate = feed!.Releases.Where(r => r.Version > current).OrderByDescending(r => r.Version).FirstOrDefault();
        var envelope = verified.Envelope;
        if (candidate is null)
        {
            return new UpdateCheck(UpdateCheckOutcome.UpToDate, null, version, null, null, now, envelope.Kid, envelope.ExpiresAt);
        }

        return new UpdateCheck(UpdateCheckOutcome.UpdateAvailable, candidate, version, null, null, now, envelope.Kid, envelope.ExpiresAt);
    }
}
