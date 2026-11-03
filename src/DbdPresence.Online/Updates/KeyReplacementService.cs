using System.Text;
using DbdPresence.Core.Online;
using DbdPresence.Packs.Cache;
using DbdPresence.Packs.Releases;
using DbdPresence.Packs.Signing;

namespace DbdPresence.Online.Updates;

/// <summary>
/// The recovery-key flow in the app (PLAN_V2 §6.2, ADR-0012, NFR-SVC-05): reads the newest key-replacement document, accepts it only with a
/// signature of a compiled-in recovery key (purpose <c>key-replacement</c>, channel <c>all</c>, not expired, not older than one already
/// applied), keeps it on disk and switches the verifier to the replaced key set. At start the kept copy is verified again and applied before
/// any other signed document is read, so a revoked key stays revoked offline and across restarts.
/// </summary>
public sealed class KeyReplacementService
{
    private readonly IReleaseTransport _transport;
    private readonly SignedDocumentVerifier _verifier;
    private readonly IReadOnlyDictionary<string, TrustedKey> _builtIn;
    private readonly IPackFileStore _files;
    private readonly string _path;
    private readonly IOnlineLog _log;
    private readonly Lock _lock = new();
    private int _refreshing;
    private KeyReplacement? _applied;

    /// <summary>Initializes a new instance of the <see cref="KeyReplacementService"/> class.</summary>
    /// <param name="transport">Where the document comes from.</param>
    /// <param name="verifier">The verifier whose key set is replaced.</param>
    /// <param name="builtIn">The compiled-in keys (the replacement is always applied to these, never to an earlier result).</param>
    /// <param name="files">File access.</param>
    /// <param name="path">Where the applied document is kept.</param>
    /// <param name="log">Diagnostics.</param>
    public KeyReplacementService(IReleaseTransport transport, SignedDocumentVerifier verifier, IReadOnlyDictionary<string, TrustedKey> builtIn, IPackFileStore files, string path, IOnlineLog log)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(builtIn);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(log);
        _transport = transport;
        _verifier = verifier;
        _builtIn = builtIn;
        _files = files;
        _path = path;
        _log = log;
    }

    /// <summary>Gets the version of the applied replacement, or null when none is applied.</summary>
    public long? AppliedVersion { get; private set; }

    /// <summary>Gets the key ids revoked by the applied replacement.</summary>
    public IReadOnlyList<string> Revoked { get; private set; } = [];

    /// <summary>
    /// Gets a value indicating whether a kept replacement exists but could not be read, verified or applied (security review 2026-10-25, L2).
    /// The compiled-in keys would then trust a key that was revoked, so updates stay off (fail closed) until a fresh replacement is fetched
    /// and applied; other signed documents keep using the compiled-in keys.
    /// </summary>
    public bool IsDamaged { get; private set; }

    /// <summary>Applies the kept document, if there is one and it still verifies. Call once at start, before other documents are verified.</summary>
    public void LoadStored()
    {
        byte[] bytes;
        try
        {
            if (!_files.Exists(_path))
            {
                return;
            }

            bytes = _files.ReadAllBytes(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Damaged("it could not be read (" + ex.GetType().Name + ")");
            return;
        }

        var result = _verifier.VerifyStored(bytes, SignedPurposes.KeyReplacement, KeyReplacement.Channel);
        if (!result.IsValid)
        {
            Damaged("it no longer verifies (" + result.Outcome + ")");
            return;
        }

        if (!KeyReplacement.TryParse(result.Envelope!.PayloadJson, out var replacement, out var error))
        {
            Damaged("its content is not valid (" + error + ")");
            return;
        }

        Apply(result.Envelope, replacement!);
        _verifier.CommitVersion(result.Envelope);
    }

    /// <summary>
    /// Reads the newest document from the service and applies it when it is valid, newer than the applied one and keeps every revocation of
    /// the applied one (replacements are cumulative; security review 2026-10-25, M1).
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when a new replacement was applied.</returns>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        // The update check and the configuration refresh both call this (TM-04): one at a time, so the file is never written twice at once.
        // A call that finds another running returns false; the next check or refresh tries again.
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            return await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    private async Task<bool> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        var answer = await _transport.GetKeyReplacementAsync(cancellationToken).ConfigureAwait(false);
        if (!answer.IsOk)
        {
            // 404 no_key_replacement is the normal case; any other failure is retried at the next check.
            return false;
        }

        var bytes = Encoding.UTF8.GetBytes(answer.Value!);
        var result = _verifier.Verify(bytes, SignedPurposes.KeyReplacement, KeyReplacement.Channel);
        if (!result.IsValid)
        {
            _log.Warning("Key replacement refused: " + result.Outcome);
            return false;
        }

        if (AppliedVersion is { } applied && result.Envelope!.Version <= applied)
        {
            return false;
        }

        if (!KeyReplacement.TryParse(result.Envelope!.PayloadJson, out var replacement, out var error))
        {
            _log.Warning("Key replacement refused: " + error);
            return false;
        }

        KeyReplacement? earlier;
        lock (_lock)
        {
            earlier = _applied;
        }

        if (earlier is not null && !replacement!.KeepsRevocationsOf(earlier))
        {
            _log.Warning("Key replacement refused: it leaves out a key that an earlier replacement revoked.");
            return false;
        }

        try
        {
            _files.WriteAllBytesDurable(_path + ".tmp", bytes);
            _files.Move(_path + ".tmp", _path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not kept, so not applied either: applying a replacement that would be lost at the next start would make trust flip-flop.
            _log.Warning("Key replacement could not be kept: " + ex.GetType().Name);
            return false;
        }

        Apply(result.Envelope, replacement!);
        _verifier.CommitVersion(result.Envelope);
        _log.Warning($"Key replacement {result.Envelope.Version} applied: {Revoked.Count} key(s) revoked.");
        return true;
    }

    private void Apply(SignedEnvelope envelope, KeyReplacement replacement)
    {
        var application = replacement.Apply(_builtIn);
        foreach (var line in application.Skipped)
        {
            _log.Warning("Key replacement entry not applied: " + line);
        }

        try
        {
            // Also drops the version floors committed under the revoked keys (TM-01).
            _verifier.UseKeys(new TrustedKeySet(application.Effective));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The new keys are in use and the revoked floors are dropped in memory; only the file still lists them, and the next start drops them again.
            _log.Warning("The version floors of the revoked keys could not be saved (" + ex.GetType().Name + "); they are ignored until the next start.");
        }

        lock (_lock)
        {
            _applied = replacement;
            AppliedVersion = envelope.Version;
            Revoked = replacement.Revoke;
            IsDamaged = false;
        }
    }

    private void Damaged(string why)
    {
        IsDamaged = true;
        _log.Warning($"The kept key replacement is damaged: {why}. Updates stay off until a fresh key replacement is fetched from the service.");
    }
}
