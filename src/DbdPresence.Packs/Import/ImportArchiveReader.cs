using System.IO.Compression;
using System.Text;
using DbdPresence.Packs.Roster;

namespace DbdPresence.Packs.Import;

/// <summary>
/// Reads a user-selected NightLight/zip archive entirely in memory (FR-PACK-07). Entries are never extracted to disk
/// and entry names are never used as paths. Sizes are counted from the bytes actually read, never from the declared
/// <c>Length</c>. The images it returns are still untrusted: they must be decoded by the image helper process
/// (FR-PACK-10).
/// </summary>
public sealed class ImportArchiveReader
{
    private const int UnixTypeMask = 0xF000;
    private const int UnixSymlink = 0xA000;
    private static readonly byte[] s_pngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] s_jpegMagic = [0xFF, 0xD8, 0xFF];

    private static readonly HashSet<string> s_deviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly ImportLimits _limits;
    private readonly CharacterRoster _roster;

    /// <summary>Creates a reader.</summary>
    /// <param name="roster">The characters that file names are matched against (by code, then by name).</param>
    /// <param name="limits">Archive limits; the shipped ones by default.</param>
    public ImportArchiveReader(CharacterRoster roster, ImportLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(roster);
        _roster = roster;
        _limits = limits ?? ImportLimits.Default;
    }

    /// <summary>Reads the archive. Never throws for hostile content; a refusal is reported in the result.</summary>
    public ImportResult Read(Stream archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (!archive.CanSeek)
        {
            return ImportResult.Reject("The file could not be read as an archive.");
        }

        if (archive.Length > _limits.MaxArchiveBytes)
        {
            return ImportResult.Reject($"The file is larger than {_limits.MaxArchiveBytes / (1024 * 1024)} MB.");
        }

        try
        {
            archive.Position = 0;
            using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
            return ReadEntries(zip);
        }
        catch (InvalidDataException)
        {
            return ImportResult.Reject("The file is not a valid zip archive.");
        }
    }

    private ImportResult ReadEntries(ZipArchive zip)
    {
        if (zip.Entries.Count > _limits.MaxEntries)
        {
            return ImportResult.Reject($"The archive has more than {_limits.MaxEntries} files.");
        }

        var candidates = new List<ImportCandidate>();
        var skipped = new List<SkippedEntry>();
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            var display = Sanitize(entry.FullName);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                skipped.Add(new SkippedEntry(display, "A folder, not an image."));
                continue;
            }

            var nameProblem = CheckName(entry);
            if (nameProblem is not null)
            {
                skipped.Add(new SkippedEntry(display, nameProblem));
                continue;
            }

            var outcome = ReadEntry(entry, out var bytes, out var failure, out var inflated);

            // Every inflated byte counts toward the budget, also those of skipped (over-size or damaged) entries, so the total
            // inflate work of one archive is bounded by MaxTotalBytes plus one entry (threat-model review I-5).
            total += inflated;
            if (outcome != EntryOutcome.Refuse && total > _limits.MaxTotalBytes)
            {
                return ImportResult.Reject($"The archive expands to more than {_limits.MaxTotalBytes / (1024 * 1024)} MB.", skipped);
            }

            if (outcome == EntryOutcome.Refuse)
            {
                return ImportResult.Reject(failure!, skipped);
            }

            if (outcome == EntryOutcome.Skip)
            {
                skipped.Add(new SkippedEntry(display, failure!));
                continue;
            }

            var format = DetectFormat(bytes!, out var rejection);
            if (format is null)
            {
                skipped.Add(new SkippedEntry(display, rejection!));
                continue;
            }

            candidates.Add(new ImportCandidate(display, bytes!, format.Value, MappingProposer.Propose(display, _roster)));
        }

        return new ImportResult(candidates, skipped, null);
    }

    private EntryOutcome ReadEntry(ZipArchiveEntry entry, out byte[]? bytes, out string? failure, out long inflated)
    {
        bytes = null;
        failure = null;
        inflated = 0;
        try
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            var cap = _limits.MaxEntryBytes;

            // Stop one byte past the cap: the declared Length is never trusted, only what is actually produced.
            while (buffer.Length <= cap)
            {
                var want = (int)Math.Min(chunk.Length, cap + 1 - buffer.Length);
                var n = stream.Read(chunk, 0, want);
                if (n == 0)
                {
                    break;
                }

                buffer.Write(chunk, 0, n);
                inflated += n;
            }

            var compressed = Math.Max(1, entry.CompressedLength);
            if (buffer.Length / compressed > _limits.MaxRatio)
            {
                failure = "The archive expands far more than a normal image file (possible zip bomb).";
                return EntryOutcome.Refuse;
            }

            if (buffer.Length > cap)
            {
                failure = $"Larger than {cap / (1024 * 1024)} MB.";
                return EntryOutcome.Skip;
            }

            bytes = buffer.ToArray();
            return EntryOutcome.Accept;
        }
        catch (InvalidDataException ex)
        {
            failure = $"Damaged entry ({ex.Message}).";
            return EntryOutcome.Skip;
        }
        catch (IOException ex)
        {
            failure = $"Could not be read ({ex.Message}).";
            return EntryOutcome.Skip;
        }
    }

    private static string? CheckName(ZipArchiveEntry entry)
    {
        var name = entry.FullName;
        if (name.Contains("..", StringComparison.Ordinal))
        {
            return "The name contains '..'.";
        }

        if (name.Contains(':', StringComparison.Ordinal))
        {
            return "The name contains ':'.";
        }

        if (name.StartsWith('/') || name.StartsWith('\\') || Path.IsPathRooted(name))
        {
            return "The name is an absolute path.";
        }

        foreach (var segment in name.Split('/', '\\'))
        {
            var stem = segment.TrimEnd(' ', '.');
            var dot = stem.IndexOf('.', StringComparison.Ordinal);
            if (dot >= 0)
            {
                stem = stem[..dot];
            }

            if (s_deviceNames.Contains(stem.TrimEnd(' ')))
            {
                return "The name is a reserved device name.";
            }
        }

        if (((entry.ExternalAttributes >> 16) & UnixTypeMask) == UnixSymlink)
        {
            return "A symbolic link.";
        }

        return null;
    }

    private static ImportImageFormat? DetectFormat(byte[] data, out string? rejection)
    {
        rejection = null;
        if (data.AsSpan().StartsWith(s_pngMagic))
        {
            return ImportImageFormat.Png;
        }

        if (data.AsSpan().StartsWith(s_jpegMagic))
        {
            return ImportImageFormat.Jpeg;
        }

        if (data.Length >= 12 && data.AsSpan(0, 4).SequenceEqual("RIFF"u8) && data.AsSpan(8, 4).SequenceEqual("WEBP"u8))
        {
            return ImportImageFormat.WebP;
        }

        var head = Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 512));
        rejection = head.Contains("<svg", StringComparison.OrdinalIgnoreCase)
            ? "SVG images are not accepted."
            : "Not a PNG, JPEG or WebP image.";
        return null;
    }

    private static string Sanitize(string entryName)
    {
        var slash = Math.Max(entryName.LastIndexOf('/'), entryName.LastIndexOf('\\'));
        var leaf = slash >= 0 ? entryName[(slash + 1)..] : entryName;
        var builder = new StringBuilder(Math.Min(leaf.Length, 64));
        foreach (var c in leaf)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '(' or ')' ? c : '_');
            if (builder.Length >= 64)
            {
                break;
            }
        }

        var result = builder.ToString().Trim();
        return result.Length == 0 ? "unnamed" : result;
    }

    private enum EntryOutcome
    {
        Accept,
        Skip,
        Refuse,
    }
}
