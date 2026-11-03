using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbdPresence.Packs.Releases;
using DbdPresence.Packs.Signing;

namespace DbdPresence.ReleaseSigner;

/// <summary>Asks the owner for a passphrase without showing it.</summary>
internal interface IPassphrasePrompt
{
    /// <summary>Reads a passphrase.</summary>
    /// <param name="prompt">What to ask.</param>
    /// <returns>The passphrase.</returns>
    string Read(string prompt);
}

/// <summary>Thrown for a mistake the owner can fix (a wrong option, a missing file); the message is shown as it is.</summary>
internal sealed class SignerException : Exception
{
    public SignerException()
    {
    }

    public SignerException(string message)
        : base(message)
    {
    }

    public SignerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The tool's commands. Each one writes plain-language output for the owner; see docs/owner/release-keys.md.</summary>
internal sealed class Commands
{
    /// <summary>Default lifetime of a release feed: the most the app accepts (ADR-0012).</summary>
    public static readonly TimeSpan FeedLifetime = SignedPurposes.MaxLifetime(SignedPurposes.Releases)!.Value;

    /// <summary>Lifetime of a key replacement: the most the app accepts.</summary>
    public static readonly TimeSpan ReplacementLifetime = SignedPurposes.MaxLifetime(SignedPurposes.KeyReplacement)!.Value;

    private static readonly string[] s_channels = ["stable", "beta"];
    private readonly TextWriter _out;
    private readonly IPassphrasePrompt _prompt;
    private readonly TimeProvider _time;

    public Commands(TextWriter output, IPassphrasePrompt prompt, TimeProvider time)
    {
        _out = output;
        _prompt = prompt;
        _time = time;
    }

    /// <summary>Makes a key pair; writes the encrypted private key and the public entry to <paramref name="directory"/>.</summary>
    public PublicKeyEntry KeyGen(OfflineKeyKind kind, string kid, string directory)
    {
        ReleaseKeys.RequireKid(kid);
        ReleaseKeys.RefuseRepositoryFolder(directory);
        Directory.CreateDirectory(directory);
        var privatePath = Path.Combine(directory, kid + ".private.pem");
        var publicPath = Path.Combine(directory, kid + ".public.json");
        if (File.Exists(privatePath) || File.Exists(publicPath))
        {
            throw new SignerException($"A key named {kid} already exists in {directory}. Choose another name (for example {NextName(kid)}); never overwrite a key.");
        }

        var passphrase = _prompt.Read($"Choose a passphrase for {kid} (at least {ReleaseKeys.MinPassphraseLength} characters; write it down and keep it apart from the drive): ");
        try
        {
            ReleaseKeys.RequirePassphrase(passphrase);
        }
        catch (ArgumentException ex)
        {
            throw new SignerException(ex.Message, ex);
        }

        if (_prompt.Read("Type the passphrase again: ") != passphrase)
        {
            throw new SignerException("The two passphrases differ. Nothing was written.");
        }

        var (key, entry) = ReleaseKeys.Generate(kind, kid);
        using (key)
        {
            var pem = ReleaseKeys.ExportEncrypted(key, passphrase);
            using (var check = ReleaseKeys.ImportEncrypted(pem, passphrase))
            {
                if (!check.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(entry.Spki))
                {
                    throw new InvalidOperationException("The encrypted key did not read back; nothing was written.");
                }
            }

            WriteNew(privatePath, pem, Encoding.ASCII);
            WriteNew(publicPath, entry.ToJson() + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        _out.WriteLine($"Made the {(kind == OfflineKeyKind.Release ? "release" : "recovery")} key {kid}.");
        _out.WriteLine($"  Private key (encrypted): {privatePath}");
        _out.WriteLine($"  Public key:              {publicPath}");
        _out.WriteLine($"  Fingerprint:             {entry.Fingerprint}");
        _out.WriteLine();
        _out.WriteLine("Public entry for src/DbdPresence.Online/TrustedServiceKeys.json and service/wrangler.toml (OFFLINE_PUBLIC_KEYS):");
        _out.WriteLine(entry.ToJson());
        return entry;
    }

    /// <summary>
    /// Signs a release feed for the packages in <paramref name="packagesDirectory"/> (the newest three). Each listed package must be an app
    /// build for <paramref name="set"/> (<see cref="BuildCheck"/>): a staging build is never signed for production.
    /// </summary>
    public string SignFeed(string keyPath, string? kid, string channel, string set, string packagesDirectory, string outputPath, string? notesPath, string? previousPath, long? version, TimeSpan? lifetime)
    {
        RequireChannel(channel);
        BuildCheck.RequireSet(set);
        var paths = PackageReader.FindFullPackages(packagesDirectory, channel);
        if (paths.Count == 0)
        {
            throw new SignerException($"No DbdPresence-<version>-{channel}-full.nupkg in {packagesDirectory}. Point --packages at the folder the installer step wrote (installer\\vpk).");
        }

        var notes = notesPath is null ? null : File.ReadAllText(notesPath, Encoding.UTF8).Trim();
        var packages = new List<ReleasePackage>();
        try
        {
            packages.AddRange(paths.Select(p => PackageReader.Read(p, channel)));
        }
        catch (InvalidDataException ex)
        {
            throw new SignerException(ex.Message, ex);
        }

        packages.Sort((a, b) => b.Version.CompareTo(a.Version));
        var kept = packages.Take(ReleaseFeed.MaxReleases).Select((p, i) => i == 0 && !string.IsNullOrEmpty(notes) ? p with { Notes = notes } : p).ToList();
        foreach (var p in kept)
        {
            BuildCheck.Require(Path.Combine(packagesDirectory, p.FileName), set);
        }

        var feed = new ReleaseFeed(ReleaseFeed.ExpectedPackageId, channel, kept);
        (SignedEnvelope Envelope, ReleaseFeed Feed)? previous = previousPath is null ? null : ReadPreviousFeed(previousPath, channel);
        if (previous is not null && previous.Value.Feed.Newest is { } before && feed.Newest!.Version < before.Version)
        {
            throw new SignerException(
                $"The newest package ({feed.Newest.Version}) is lower than the newest one already published ({before.Version}). The app never installs a lower version. " +
                "To roll back, pack the older build again with a new, higher version number and sign that.");
        }

        return SignAndWrite(keyPath, kid, SignedPurposes.Releases, channel, feed.ToPayloadJson(), outputPath, previous?.Envelope.Version, version, lifetime ?? FeedLifetime, () =>
        {
            foreach (var p in feed.Releases)
            {
                _out.WriteLine($"  {p.Version,-16} {p.FileName}  {p.Size:N0} bytes  sha256 {p.Sha256}");
            }
        });
    }

    /// <summary>Signs the same feed again with new times and a higher version (a feed expires after 30 days).</summary>
    public string Resign(string keyPath, string? kid, string feedPath, string outputPath, TimeSpan? lifetime)
    {
        var (envelope, feed) = ReadPreviousFeed(feedPath, channel: null);
        return SignAndWrite(keyPath, kid, SignedPurposes.Releases, envelope.Channel, feed.ToPayloadJson(), outputPath, envelope.Version, null, lifetime ?? FeedLifetime, () =>
        {
            foreach (var p in feed.Releases)
            {
                _out.WriteLine($"  {p.Version,-16} {p.FileName}");
            }
        });
    }

    /// <summary>
    /// Signs a key replacement with the recovery key. Cumulative: everything in <paramref name="previousPath"/> is kept, and it is required
    /// unless <paramref name="first"/> says this is the first replacement (security review 2026-10-25, M1: leaving it out would trust a
    /// revoked key again).
    /// </summary>
    public string SignKeyReplacement(string keyPath, string? kid, IReadOnlyList<string> revoke, IReadOnlyList<string> addPublicKeyPaths, string? previousPath, string outputPath, bool first = false)
    {
        if (previousPath is null && !first)
        {
            throw new SignerException("Give the last key replacement you signed with --previous, so its revocations are kept. If this is the very first one, add --first.");
        }

        if (previousPath is not null && first)
        {
            throw new SignerException("--first and --previous cannot be used together.");
        }

        var revoked = new List<string>();
        var added = new List<AddedKey>();
        long? previousVersion = null;
        if (previousPath is not null)
        {
            var envelope = ReadEnvelope(previousPath);
            KeyReplacement? earlier = null;
            string? error = null;
            if (envelope.Purpose != SignedPurposes.KeyReplacement || !KeyReplacement.TryParse(envelope.PayloadJson, out earlier, out error))
            {
                throw new SignerException($"{previousPath} is not a key replacement ({error ?? envelope.Purpose}).");
            }

            revoked.AddRange(earlier!.Revoke);
            added.AddRange(earlier.Add);
            previousVersion = envelope.Version;
        }

        revoked.AddRange(revoke.Where(k => !revoked.Contains(k, StringComparer.Ordinal)));
        var signer = kid ?? KidFromPath(keyPath);
        if (revoke.FirstOrDefault(k => k == signer || k.StartsWith("recovery-", StringComparison.Ordinal)) is { } recovery)
        {
            // Refused before anything is signed (final review LOW): apps and the service never honour such an entry, so signing it could only mislead.
            throw new SignerException($"{recovery} is a recovery key. A key replacement can never revoke a recovery key; a new recovery key needs a new app release.");
        }

        foreach (var path in addPublicKeyPaths)
        {
            PublicKeyEntry entry;
            try
            {
                entry = PublicKeyEntry.Parse(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (FormatException ex)
            {
                throw new SignerException($"{path}: {ex.Message}", ex);
            }

            if (entry.Purposes.Contains(SignedPurposes.KeyReplacement))
            {
                throw new SignerException("A key replacement cannot add a recovery key; a new recovery key needs a new app release.");
            }

            added.RemoveAll(k => k.Kid == entry.Kid);
            added.Add(new AddedKey(entry.Kid, entry.Spki, entry.Purposes));
        }

        added.RemoveAll(k => revoked.Contains(k.Kid, StringComparer.Ordinal));
        string payload;
        try
        {
            payload = new KeyReplacement(revoked, added).ToPayloadJson();
        }
        catch (ArgumentException ex)
        {
            throw new SignerException(ex.Message, ex);
        }

        return SignAndWrite(keyPath, kid, SignedPurposes.KeyReplacement, KeyReplacement.Channel, payload, outputPath, previousVersion, null, ReplacementLifetime, () =>
        {
            _out.WriteLine("  Revoked: " + (revoked.Count == 0 ? "(none)" : string.Join(", ", revoked)));
            _out.WriteLine("  Added:   " + (added.Count == 0 ? "(none)" : string.Join(", ", added.Select(k => $"{k.Kid} ({string.Join("+", k.Purposes)})"))));
        });
    }

    /// <summary>
    /// Verifies a document against a compiled-in key list, as the app would; optionally checks the packages a feed lists: their size and
    /// SHA-256, and that each one is an app build for <paramref name="set"/> (a staging build in a production feed fails).
    /// </summary>
    public bool Verify(string documentPath, string trustPath, string set, string? packagesDirectory)
    {
        BuildCheck.RequireSet(set);
        var text = File.ReadAllText(documentPath, Encoding.UTF8).Trim();
        var envelope = ReadEnvelope(documentPath);
        var keys = LoadTrust(trustPath, set);
        var verifier = new SignedDocumentVerifier(new TrustedKeySet(keys), new InMemoryVersionStore(), _time);
        var result = verifier.Verify(Encoding.UTF8.GetBytes(text), envelope.Purpose, envelope.Channel);
        _out.WriteLine($"{Path.GetFileName(documentPath)}: {envelope.Purpose} for channel {envelope.Channel}, version {envelope.Version}, signed by {envelope.Kid}, expires {DocumentSigner.Timestamp(envelope.ExpiresAt)}.");
        _out.WriteLine($"Result against the {set} keys: {result.Outcome}");
        if (!result.IsValid)
        {
            return false;
        }

        if (envelope.Purpose == SignedPurposes.Releases && packagesDirectory is not null)
        {
            if (!ReleaseFeed.TryParse(envelope.PayloadJson, envelope.Channel, out var feed, out var error))
            {
                _out.WriteLine("The feed's payload is not valid: " + error);
                return false;
            }

            var ok = true;
            foreach (var p in feed!.Releases)
            {
                var path = Path.Combine(packagesDirectory, p.FileName);
                bool match;
                try
                {
                    match = File.Exists(path) && PackageReader.Read(path, envelope.Channel) is { } local && local.Sha256 == p.Sha256 && local.Size == p.Size;
                }
                catch (InvalidDataException ex)
                {
                    _out.WriteLine($"  {p.FileName}: {ex.Message}");
                    match = false;
                }

                _out.WriteLine($"  {p.FileName}: {(match ? "matches" : "MISSING OR DIFFERENT")}");
                ok &= match;
                if (match)
                {
                    try
                    {
                        BuildCheck.Require(path, set);
                        _out.WriteLine($"  {p.FileName}: a {set} build");
                    }
                    catch (SignerException ex)
                    {
                        _out.WriteLine($"  {p.FileName}: WRONG BUILD. {ex.Message}");
                        ok = false;
                    }
                }
            }

            return ok;
        }

        return true;
    }

    /// <summary>Checks that every full package of <paramref name="channel"/> in the folder is an app build for <paramref name="set"/>; signs nothing.</summary>
    public bool CheckBuild(string packagesDirectory, string channel, string set)
    {
        RequireChannel(channel);
        BuildCheck.RequireSet(set);
        var paths = PackageReader.FindFullPackages(packagesDirectory, channel);
        if (paths.Count == 0)
        {
            throw new SignerException($"No DbdPresence-<version>-{channel}-full.nupkg in {packagesDirectory}.");
        }

        var ok = true;
        foreach (var path in paths)
        {
            try
            {
                BuildCheck.Require(path, set);
                _out.WriteLine($"  {Path.GetFileName(path)}: a {set} build");
            }
            catch (SignerException ex)
            {
                _out.WriteLine($"  {Path.GetFileName(path)}: WRONG BUILD. {ex.Message}");
                ok = false;
            }
        }

        return ok;
    }

    private static string NextName(string kid)
    {
        var dash = kid.LastIndexOf('-');
        return dash > 0 && int.TryParse(kid[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? $"{kid[..dash]}-{n + 1}" : kid + "-2";
    }

    private static void RequireChannel(string channel)
    {
        if (!s_channels.Contains(channel, StringComparer.Ordinal))
        {
            throw new SignerException("The channel is stable or beta.");
        }
    }

    private static SignedEnvelope ReadEnvelope(string path)
    {
        try
        {
            return DocumentSigner.ReadEnvelopeUnverified(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (FormatException ex)
        {
            throw new SignerException($"{path}: {ex.Message}", ex);
        }
    }

    private static (long Version, ReleaseFeed Feed) ReadPreviousFeedCore(SignedEnvelope envelope, string path)
    {
        ReleaseFeed? feed = null;
        string? error = null;
        if (envelope.Purpose != SignedPurposes.Releases || !ReleaseFeed.TryParse(envelope.PayloadJson, envelope.Channel, out feed, out error))
        {
            throw new SignerException($"{path} is not a release feed ({error ?? envelope.Purpose}).");
        }

        return (envelope.Version, feed!);
    }

    private static Dictionary<string, TrustedKey> LoadTrust(string path, string set)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            var keys = new Dictionary<string, TrustedKey>(StringComparer.Ordinal);
            foreach (var item in json.RootElement.GetProperty(set).EnumerateArray())
            {
                if (item.TryGetProperty("awaitingKey", out var awaiting) && awaiting.ValueKind == JsonValueKind.True)
                {
                    continue;
                }

                keys[item.GetProperty("kid").GetString()!] = new TrustedKey(
                    Base64Url.DecodeFromChars(item.GetProperty("spki").GetString()!),
                    [.. item.GetProperty("purposes").EnumerateArray().Select(p => p.GetString()!)]);
            }

            return keys;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new SignerException($"{path} is not a trusted key list with a '{set}' section.", ex);
        }
    }

    private static (SignedEnvelope Envelope, ReleaseFeed Feed) ReadPreviousFeed(string path, string? channel)
    {
        var envelope = ReadEnvelope(path);
        var (_, feed) = ReadPreviousFeedCore(envelope, path);
        if (channel is not null && envelope.Channel != channel)
        {
            throw new SignerException($"{path} is for the {envelope.Channel} channel, not {channel}.");
        }

        return (envelope, feed);
    }

    private string SignAndWrite(string keyPath, string? kid, string purpose, string channel, string payload, string outputPath, long? previousVersion, long? version, TimeSpan lifetime, Action describe)
    {
        if (File.Exists(outputPath))
        {
            throw new SignerException($"{outputPath} already exists. Write the new document to a new file.");
        }

        var issuedAt = _time.GetUtcNow();
        issuedAt = issuedAt.AddTicks(-(issuedAt.Ticks % TimeSpan.TicksPerMillisecond));
        var nextVersion = version ?? Math.Max(issuedAt.ToUnixTimeSeconds(), (previousVersion ?? 0) + 1);
        if (nextVersion > SignedDocumentVerifier.MaxVersion)
        {
            throw new SignerException($"The version must be at most {SignedDocumentVerifier.MaxVersion}.");
        }

        if (previousVersion is { } before && nextVersion <= before)
        {
            throw new SignerException($"The version must be higher than {before}, the version of the earlier document.");
        }

        var name = kid ?? KidFromPath(keyPath);
        var pem = File.ReadAllText(keyPath, Encoding.ASCII);
        var passphrase = _prompt.Read($"Passphrase for {name}: ");
        ECDsa key;
        try
        {
            key = ReleaseKeys.ImportEncrypted(pem, passphrase);
        }
        catch (CryptographicException ex)
        {
            throw new SignerException("The passphrase is wrong or the key file is damaged. Nothing was signed.", ex);
        }

        string document;
        using (key)
        {
            try
            {
                document = DocumentSigner.Sign(key, name, purpose, channel, nextVersion, issuedAt, issuedAt + lifetime, payload);
            }
            catch (ArgumentException ex)
            {
                throw new SignerException(ex.Message, ex);
            }
        }

        File.WriteAllText(outputPath, document, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _out.WriteLine($"Signed {purpose} for channel {channel}, version {nextVersion}, with {name}:");
        describe();
        _out.WriteLine($"  Valid until {DocumentSigner.Timestamp(issuedAt + lifetime)}{(purpose == SignedPurposes.Releases ? " (sign it again with the resign command before then)" : string.Empty)}.");
        _out.WriteLine($"  Written to {outputPath}");
        return document;
    }

    /// <summary>Writes a file that must not exist yet (CreateNew), so a key file is never overwritten, not even in a race.</summary>
    internal static void WriteNew(string path, string text, Encoding encoding)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, encoding);
        writer.Write(text);
    }

    private static string KidFromPath(string keyPath)
    {
        const string Suffix = ".private.pem";
        var name = Path.GetFileName(keyPath);
        if (!name.EndsWith(Suffix, StringComparison.Ordinal))
        {
            throw new SignerException("Name the key with --kid, or keep the file name this tool gave it (<name>.private.pem).");
        }

        return name[..^Suffix.Length];
    }
}
