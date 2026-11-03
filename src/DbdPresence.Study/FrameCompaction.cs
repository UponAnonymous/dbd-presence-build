using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using DbdPresence.StudyCapture;
using DbdPresence.Vision.Evaluation;
using DbdPresence.Vision.Imaging;

namespace DbdPresence.Study;

/// <summary>What making one recording's pictures smaller did.</summary>
/// <param name="Compacted">Pictures rewritten smaller.</param>
/// <param name="AlreadyDone">Pictures that carried the marker of an earlier pass and were not looked at again.</param>
/// <param name="NoGain">Pictures that could not be made smaller; they only got the marker.</param>
/// <param name="Left">Pictures left exactly as they were because they are not the plain 8-bit RGB files the recorder writes, or could not be read.</param>
/// <param name="Refused">Pictures left exactly as they were because the new file did not decode to the same pixels (should never happen).</param>
/// <param name="BytesBefore">Size of the rewritten (or marked) pictures before.</param>
/// <param name="BytesAfter">Size of the same pictures after.</param>
/// <param name="Problems">Plain sentences about anything that could not be done.</param>
internal sealed record CompactionResult(int Compacted, int AlreadyDone, int NoGain, int Left, int Refused, long BytesBefore, long BytesAfter, IReadOnlyList<string> Problems)
{
    /// <summary>Gets the bytes saved.</summary>
    public long Saved => BytesBefore - BytesAfter;

    /// <summary>Gets whether anything was changed or went wrong, i.e. whether the owner needs to be told.</summary>
    public bool HasNews => Compacted + NoGain + Left + Refused > 0 || Problems.Count > 0;
}

/// <summary>
/// Makes a recording's <c>frame-NNNNN.png</c> files smaller without changing a single pixel (FR-DET-24, DETECTION_VALIDATION §12.5 and §21 data retention). The
/// recorder writes quickly (no scanline filter, fastest deflate) so it stays light while the owner plays; afterwards, with the game closed, this pass
/// re-encodes each picture with a per-row filter and the optimal deflate level. Nothing in the evaluator depends on a picture file's bytes: the
/// manifest hash covers <c>labels.jsonl</c> only, the review file is cut from decoded pixels, and every reader decodes the picture. The pixels are what matters, so a file is
/// replaced only when the new file decodes to exactly the old pixels:
/// <list type="number">
/// <item>the old file is decoded (strict decoder) and must be a plain 8-bit RGB PNG, anything else is left as it is;</item>
/// <item>the new bytes are written to a temporary file in the same folder, read back from disk and decoded, and compared byte for byte with the old pixels;</item>
/// <item>only then the temporary file replaces the old one in one step (a crash leaves the old file or the new file, never half of one).</item>
/// </list>
/// A small text chunk right after the PNG header marks a file as done, so a second pass skips it without decoding. The temporary file is named like a
/// recorder picture (<c>frame-9NNNNNNNNNNNNNNNNNNN.png</c>) so that the 30-day clean-up, which deletes only the files it knows, also deletes a leftover from a crash.
/// </summary>
internal static partial class FrameCompaction
{
    /// <summary>The keyword of the marker chunk.</summary>
    public const string MarkerKeyword = "dbd-presence";

    /// <summary>The text of the marker chunk.</summary>
    public const string MarkerText = "compacted-1";

    private const int IhdrEnd = 8 + 4 + 4 + 13 + 4; // signature + length + type + body + crc
    private const int ColourTypeOffset = 8 + 4 + 4 + 9;

    /// <summary>The bytes of the marker chunk, ready to be placed after the header chunk.</summary>
    private static readonly byte[] s_marker = MakeMarker();
    private static readonly byte[] s_signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Makes every picture of a session folder smaller. Pictures are handled in parallel on a few cores; the result does not depend on the order.</summary>
    /// <param name="folder">The recording's folder (one the tool may touch: the caller found it through <see cref="StudySessions.RecordingFolders"/>).</param>
    /// <param name="progress">Told in plain words how far it is.</param>
    /// <param name="encoder">Replaces the encoder (width, height, B-G-R-A pixels, returns a PNG). Null in use; tests give a faulty one to show that a wrong result is refused.</param>
    public static CompactionResult Run(string folder, Action<string> progress, Func<int, int, byte[], byte[]>? encoder = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentNullException.ThrowIfNull(progress);
        var problems = new List<string>();
        var directory = new DirectoryInfo(folder);
        if (!directory.Exists || StudyFiles.IsLink(directory))
        {
            problems.Add("the recording's folder is missing or is a link, so nothing was changed");
            return new CompactionResult(0, 0, 0, 0, 0, 0, 0, problems);
        }

        RemoveLeftovers(directory, problems);
        var files = new List<FileInfo>();
        foreach (var entry in directory.EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            if (entry is FileInfo file && FrameFileName().IsMatch(file.Name))
            {
                if (StudyFiles.IsLink(file))
                {
                    problems.Add($"{file.Name} is a link, so it was not followed and not changed");
                }
                else
                {
                    files.Add(file);
                }
            }
        }

        var pending = new List<FileInfo>();
        var already = 0;
        foreach (var file in files)
        {
            try
            {
                if (IsMarked(file.FullName))
                {
                    already++;
                }
                else
                {
                    pending.Add(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{file.Name} was left as it was: it could not be read ({ex.Message})");
            }
        }

        var compacted = 0;
        var noGain = 0;
        var left = 0;
        var refused = 0;
        long before = 0;
        long after = 0;
        var done = 0;
        var gate = new object();
        if (pending.Count > 0)
        {
            progress($"Making {pending.Count} pictures smaller without changing a pixel. This takes a few minutes the first time.");
        }

        var step = Math.Max(1, pending.Count / 10);
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(System.Environment.ProcessorCount / 2, 1, 4) };
        Parallel.For(0, pending.Count, options, i =>
        {
            var outcome = Process(pending[i], i, encoder);
            lock (gate)
            {
                switch (outcome.Kind)
                {
                    case Kind.Compacted:
                        compacted++;
                        before += outcome.Before;
                        after += outcome.After;
                        break;
                    case Kind.NoGain:
                        noGain++;
                        before += outcome.Before;
                        after += outcome.After;
                        break;
                    case Kind.Refused:
                        refused++;
                        problems.Add($"{pending[i].Name} was left as it was: {outcome.Reason}");
                        break;
                    default:
                        left++;
                        problems.Add($"{pending[i].Name} was left as it was: {outcome.Reason}");
                        break;
                }

                done++;
                if (done % step == 0 && done < pending.Count)
                {
                    progress($"Making the pictures smaller: {done * 100 / pending.Count}%");
                }
            }
        });

        problems.Sort(StringComparer.Ordinal);
        return new CompactionResult(compacted, already, noGain, left, refused, before, after, problems);
    }

    /// <summary>Makes the pictures of every prepared recording smaller and says in plain words what it saved. Recordings that are done, or whose pictures are deleted, are not mentioned.</summary>
    public static void RunAll(string root, StudyEnvironment env)
    {
        foreach (var folder in StudySessions.PreparedFolders(root))
        {
            RunReported(folder, env);
        }
    }

    /// <summary>Makes one recording's pictures smaller and says what it saved.</summary>
    public static CompactionResult RunReported(string folder, StudyEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(env);
        if (!env.CompactPictures)
        {
            return new CompactionResult(0, 0, 0, 0, 0, 0, 0, []);
        }

        var id = Path.GetFileName(folder);
        var result = Run(folder, m => env.Out.WriteLine($"Recording {id}: {m}"));
        if (result.Compacted + result.NoGain > 0)
        {
            var share = result.BytesBefore > 0 ? result.Saved * 100.0 / result.BytesBefore : 0.0;
            env.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Recording {id}: {result.Compacted + result.NoGain} pictures checked and rewritten. They took {SizeText.Of(result.BytesBefore)}, now {SizeText.Of(result.BytesAfter)}: {SizeText.Of(Math.Max(0, result.Saved))} saved ({share:0}%). Every picture was decoded again and has exactly the same pixels as before."));
        }

        foreach (var problem in result.Problems.Take(5))
        {
            env.Out.WriteLine($"Recording {id}: {problem}.");
        }

        if (result.Problems.Count > 5)
        {
            env.Out.WriteLine($"Recording {id}: and {result.Problems.Count - 5} more pictures were left as they were.");
        }

        return result;
    }

    /// <summary>Tells whether a picture file already carries the marker of an earlier pass (reads the first bytes only).</summary>
    public static bool IsMarked(string path)
    {
        Span<byte> head = stackalloc byte[IhdrEnd + 64];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return HasMarker(head[..read]);
    }

    /// <summary>
    /// Re-encodes one PNG held in memory and returns the new bytes (with the marker), or null with the reason when the picture must be left alone. The new
    /// bytes are guaranteed to decode to the old pixels: that is checked here before they are returned.
    /// </summary>
    /// <param name="original">The old file's bytes.</param>
    /// <param name="reason">Why it was left alone, or null.</param>
    /// <param name="encoder">Replaces the encoder (null in use).</param>
    /// <param name="old">The decoded old picture (the pixels the new file must reproduce), or null when it was left alone before decoding.</param>
    /// <param name="level">The deflate level (the optimal one in use; tests may name another).</param>
    internal static byte[]? Recompress(byte[] original, out string? reason, out ImageBuffer? old, CompressionLevel level = CompressionLevel.Optimal, Func<int, int, byte[], byte[]>? encoder = null)
    {
        ArgumentNullException.ThrowIfNull(original);
        reason = null;
        old = null;
        if (original.Length < IhdrEnd || original[ColourTypeOffset] != 2 || original[ColourTypeOffset - 1] != 8)
        {
            reason = "it is not a plain 8-bit RGB picture";
            return null;
        }

        try
        {
            old = PngDecoder.Decode(original);
        }
        catch (FormatException ex)
        {
            reason = "it could not be read as a picture (" + ex.Message + ")";
            return null;
        }

        var rgba = old.Pixels;
        var bgra = new byte[rgba.Length];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            bgra[i] = rgba[i + 2];
            bgra[i + 1] = rgba[i + 1];
            bgra[i + 2] = rgba[i];
            bgra[i + 3] = 255;
        }

        var small = encoder is null ? PngEncoder.EncodeSmall(old.Width, old.Height, bgra, level) : encoder(old.Width, old.Height, bgra);
        var chosen = small.Length < original.Length && small.Length >= IhdrEnd ? small : original;
        var candidate = WithMarker(chosen);
        if (!SamePixels(old, candidate))
        {
            reason = "the new file did not decode to the same pixels";
            return null;
        }

        return candidate;
    }

    private static Outcome Process(FileInfo file, int index, Func<int, int, byte[], byte[]>? encoder)
    {
        try
        {
            var original = File.ReadAllBytes(file.FullName);
            var candidate = Recompress(original, out var reason, out var old, encoder: encoder);
            if (candidate is null)
            {
                return new Outcome(reason!.StartsWith("the new file", StringComparison.Ordinal) ? Kind.Refused : Kind.Left, 0, 0, reason);
            }

            var temporary = Path.Combine(file.DirectoryName!, TemporaryName(index));
            try
            {
                File.WriteAllBytes(temporary, candidate);

                // The check that matters: the file as it is on the disk, read back and decoded, must give the old pixels.
                var back = File.ReadAllBytes(temporary);
                if (!back.AsSpan().SequenceEqual(candidate) || !SamePixels(old!, back))
                {
                    return new Outcome(Kind.Refused, 0, 0, "the file written to the disk did not decode to the same pixels");
                }

                File.SetLastWriteTimeUtc(temporary, file.LastWriteTimeUtc);
                File.Move(temporary, file.FullName, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }

            var gained = candidate.Length < original.Length;
            return new Outcome(gained ? Kind.Compacted : Kind.NoGain, original.Length, candidate.Length, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Outcome(Kind.Left, 0, 0, "it could not be read or replaced (" + ex.Message + ")");
        }
    }

    private static bool SamePixels(ImageBuffer old, ReadOnlySpan<byte> png)
    {
        try
        {
            var other = PngDecoder.Decode(png);
            return other.Width == old.Width && other.Height == old.Height && other.Format == old.Format && other.Pixels.SequenceEqual(old.Pixels);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>The marker chunk sits right after the header chunk: length, "tEXt", keyword, NUL, text, checksum.</summary>
    private static bool HasMarker(ReadOnlySpan<byte> head) =>
        head.Length >= IhdrEnd + s_marker.Length && head[..8].SequenceEqual(s_signature) && head.Slice(IhdrEnd, s_marker.Length).SequenceEqual(s_marker);

    /// <summary>Puts the marker chunk after the header chunk of a valid PNG (the signature and the header chunk are copied unchanged).</summary>
    private static byte[] WithMarker(byte[] png)
    {
        using var stream = new MemoryStream(png.Length + s_marker.Length);
        stream.Write(png, 0, IhdrEnd);
        stream.Write(s_marker);
        stream.Write(png, IhdrEnd, png.Length - IhdrEnd);
        return stream.ToArray();
    }

    private static byte[] MakeMarker()
    {
        using var stream = new MemoryStream();
        PngEncoder.WriteChunk(stream, "tEXt"u8, Encoding.ASCII.GetBytes(MarkerKeyword + "\0" + MarkerText));
        return stream.ToArray();
    }

    /// <summary>Deletes temporary pictures an interrupted earlier pass left behind (regular files only; the name cannot clash with a recorder picture).</summary>
    private static void RemoveLeftovers(DirectoryInfo directory, List<string> problems)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos().Where(e => e is FileInfo && TemporaryFileName().IsMatch(e.Name) && !StudyFiles.IsLink(e)))
        {
            try
            {
                entry.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{entry.Name}, left over from an interrupted pass, could not be deleted: {ex.Message}");
            }
        }
    }

    private static string TemporaryName(int index) => "frame-9" + index.ToString("D19", CultureInfo.InvariantCulture) + ".png";

    [GeneratedRegex(@"^frame-\d{5,}\.png$", RegexOptions.CultureInvariant)]
    internal static partial Regex FrameFileName();

    [GeneratedRegex(@"^frame-9\d{19}\.png$", RegexOptions.CultureInvariant)]
    private static partial Regex TemporaryFileName();

    private enum Kind
    {
        Compacted,
        NoGain,
        Left,
        Refused,
    }

    private readonly record struct Outcome(Kind Kind, long Before, long After, string? Reason);
}

/// <summary>Sizes in plain words for the console.</summary>
internal static class SizeText
{
    /// <summary>Formats a byte count as "812 MB" or "1.4 GB" (binary units, one decimal for gigabytes).</summary>
    public static string Of(long bytes)
    {
        var megabytes = bytes / (1024.0 * 1024.0);
        return megabytes >= 1024
            ? string.Create(CultureInfo.InvariantCulture, $"{megabytes / 1024.0:0.0} GB")
            : string.Create(CultureInfo.InvariantCulture, $"{megabytes:0.#} MB");
    }
}
