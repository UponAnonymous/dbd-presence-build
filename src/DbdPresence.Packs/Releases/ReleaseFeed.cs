using System.Text;
using System.Text.Json;

namespace DbdPresence.Packs.Releases;

/// <summary>One full update package listed by a signed release feed (FR-ONL-10).</summary>
/// <param name="Version">The app version inside the package.</param>
/// <param name="FileName">The package file name, always <c>DbdPresence-&lt;version&gt;-&lt;channel&gt;-full.nupkg</c> (the name Velopack's packer writes
/// for a named channel).</param>
/// <param name="Size">The exact size in bytes.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the package; the download is accepted only with exactly this hash.</param>
/// <param name="Notes">Optional plain-text release notes, shown as text only.</param>
public sealed record ReleasePackage(ReleaseVersion Version, string FileName, long Size, string Sha256, string? Notes);

/// <summary>
/// The payload of a signed release feed (purpose <c>releases</c>; PLAN_V2 §6.5, ADR-0012): which full packages a channel offers, each with its
/// size and SHA-256. It is signed offline with the release key (tools/DbdPresence.ReleaseSigner) and only uploaded through the Owner Panel; the
/// service and the app apply the same rules (<see cref="TryParse"/>, <c>service/src/releases/feed.ts</c>).
/// </summary>
/// <param name="PackageId">The Velopack package id; must be <see cref="ExpectedPackageId"/>.</param>
/// <param name="Channel">The channel of the envelope the payload came in (not a payload member).</param>
/// <param name="Releases">The listed packages, newest first.</param>
public sealed record ReleaseFeed(string PackageId, string Channel, IReadOnlyList<ReleasePackage> Releases)
{
    /// <summary>The only schema version this build reads.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The app's package id (build/pack-installer.ps1 <c>--packId</c>).</summary>
    public const string ExpectedPackageId = "DbdPresence";

    /// <summary>The most packages one feed may list (owner decision D18: only the last 3 packages are kept).</summary>
    public const int MaxReleases = 3;

    /// <summary>The largest package accepted, in bytes (below the 100 MB request limit of the service's free plan).</summary>
    public const long MaxPackageBytes = 95_000_000;

    /// <summary>The longest release notes, in characters.</summary>
    public const int MaxNotesLength = 2000;

    private static readonly JsonDocumentOptions s_options = new() { MaxDepth = 8, AllowDuplicateProperties = false };

    /// <summary>Gets the newest listed package, or null when the feed lists none.</summary>
    public ReleasePackage? Newest => Releases.Count == 0 ? null : Releases[0];

    /// <summary>The file name a package of <paramref name="version"/> on <paramref name="channel"/> must have (Velopack's name for a named channel).</summary>
    /// <param name="version">The version.</param>
    /// <param name="channel">The channel.</param>
    /// <returns><c>DbdPresence-&lt;version&gt;-&lt;channel&gt;-full.nupkg</c>.</returns>
    public static string PackageFileName(ReleaseVersion version, string channel)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentException.ThrowIfNullOrEmpty(channel);
        return $"{ExpectedPackageId}-{version.Text}-{channel}-full.nupkg";
    }

    /// <summary>
    /// Reads a feed payload. Anything unexpected is refused with a short reason, never an exception: unknown or duplicate members, a wrong package
    /// id, more than <see cref="MaxReleases"/> packages, a duplicate version or hash, a file name that does not match its version, a size outside
    /// 1..<see cref="MaxPackageBytes"/>, a hash that is not 64 lower-case hex digits, or notes longer than <see cref="MaxNotesLength"/>.
    /// </summary>
    /// <param name="payloadJson">The payload member of a verified envelope.</param>
    /// <param name="channel">The envelope's channel (file names carry it).</param>
    /// <param name="feed">The feed, with its packages sorted newest first.</param>
    /// <param name="error">Why it was refused, or null.</param>
    /// <returns>True when the payload is a valid feed.</returns>
    public static bool TryParse(string payloadJson, string channel, out ReleaseFeed? feed, out string? error)
    {
        ArgumentException.ThrowIfNullOrEmpty(channel);
        feed = null;
        error = null;
        try
        {
            using var json = JsonDocument.Parse(payloadJson, s_options);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !OnlyMembers(root, "schemaVersion", "packageId", "releases"))
            {
                return Fail("unexpected_members", out error);
            }

            if (!root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var schemaValue) || schemaValue != SchemaVersion)
            {
                return Fail("unsupported_schema", out error);
            }

            if (!root.TryGetProperty("packageId", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != ExpectedPackageId)
            {
                return Fail("wrong_package_id", out error);
            }

            if (!root.TryGetProperty("releases", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > MaxReleases)
            {
                return Fail("bad_release_list", out error);
            }

            var packages = new List<ReleasePackage>();
            foreach (var item in list.EnumerateArray())
            {
                if (!TryPackage(item, channel, out var package, out error))
                {
                    return false;
                }

                if (packages.Any(p => p.Version == package!.Version))
                {
                    return Fail("duplicate_version", out error);
                }

                if (packages.Any(p => p.Sha256 == package!.Sha256))
                {
                    return Fail("duplicate_package", out error);
                }

                packages.Add(package!);
            }

            packages.Sort((a, b) => b.Version.CompareTo(a.Version));
            feed = new ReleaseFeed(ExpectedPackageId, channel, packages);
            return true;
        }
        catch (JsonException)
        {
            // Not JSON, too deep or with duplicate members: refused like any other malformed feed.
            return Fail("malformed", out error);
        }
        catch (InvalidOperationException)
        {
            // A string member holds invalid UTF-8: refused like any other malformed feed.
            return Fail("malformed", out error);
        }
    }

    /// <summary>
    /// Writes the payload JSON for signing, in a fixed member order (the inverse of <see cref="TryParse"/>). Used by the offline signing tool.
    /// </summary>
    /// <returns>Compact JSON.</returns>
    /// <exception cref="ArgumentException">The feed breaks a rule that <see cref="TryParse"/> would refuse.</exception>
    public string ToPayloadJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("packageId", PackageId);
            writer.WriteStartArray("releases");
            foreach (var package in Releases.OrderByDescending(p => p.Version))
            {
                writer.WriteStartObject();
                writer.WriteString("version", package.Version.Text);
                writer.WriteString("fileName", package.FileName);
                writer.WriteNumber("size", package.Size);
                writer.WriteString("sha256", package.Sha256);
                if (package.Notes is not null)
                {
                    writer.WriteString("notes", package.Notes);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var text = Encoding.UTF8.GetString(buffer.ToArray());
        if (!TryParse(text, Channel, out _, out var error))
        {
            throw new ArgumentException($"The release feed is not valid: {error}.");
        }

        return text;
    }

    /// <summary>Whether <paramref name="text"/> is 64 lower-case hex digits.</summary>
    /// <param name="text">The text.</param>
    /// <returns>True for a SHA-256 in the feed's form.</returns>
    public static bool IsSha256(string? text) => text is { Length: 64 } && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool TryPackage(JsonElement item, string channel, out ReleasePackage? package, out string? error)
    {
        package = null;
        error = null;
        if (item.ValueKind != JsonValueKind.Object || !OnlyMembers(item, "version", "fileName", "size", "sha256", "notes"))
        {
            return Fail("bad_release", out error);
        }

        var version = item.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? ReleaseVersion.TryParse(v.GetString()) : null;
        if (version is null)
        {
            return Fail("bad_version", out error);
        }

        if (!item.TryGetProperty("fileName", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() != PackageFileName(version, channel))
        {
            return Fail("bad_file_name", out error);
        }

        if (!item.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out var bytes) || bytes is < 1 or > MaxPackageBytes)
        {
            return Fail("bad_size", out error);
        }

        var sha = item.TryGetProperty("sha256", out var hash) && hash.ValueKind == JsonValueKind.String ? hash.GetString() : null;
        if (!IsSha256(sha))
        {
            return Fail("bad_sha256", out error);
        }

        string? notes = null;
        if (item.TryGetProperty("notes", out var n))
        {
            if (n.ValueKind != JsonValueKind.String || n.GetString()!.Length > MaxNotesLength)
            {
                return Fail("bad_notes", out error);
            }

            notes = n.GetString();
        }

        package = new ReleasePackage(version, PackageFileName(version, channel), bytes, sha!, notes);
        return true;
    }

    private static bool OnlyMembers(JsonElement element, params string[] allowed) =>
        element.EnumerateObject().All(p => allowed.Contains(p.Name, StringComparer.Ordinal));

    private static bool Fail(string reason, out string? error)
    {
        error = reason;
        return false;
    }
}
