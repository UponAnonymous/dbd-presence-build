using System.Buffers.Binary;

namespace DbdPresence.Core.Images;

/// <summary>Why a safe image load did or did not produce an image.</summary>
public enum SafeImageStatus
{
    /// <summary>A clean PNG is available.</summary>
    Ok,

    /// <summary>The file is missing, unreadable or not a PNG, JPEG or WebP image.</summary>
    BadInput,

    /// <summary>The file is over 8 MB or the image is over 4096 by 4096 pixels.</summary>
    TooLarge,

    /// <summary>The image could not be decoded.</summary>
    DecodeFailed,

    /// <summary>The helper did not finish in time and was stopped.</summary>
    TimedOut,

    /// <summary>The helper program is missing or could not be started.</summary>
    HelperUnavailable,

    /// <summary>The helper stopped itself because it used too much memory or ran too long.</summary>
    ResourceLimit,

    /// <summary>The fitted picture is still 2 MB or more (picture fit mode only).</summary>
    OutputTooLarge,
}

/// <summary>The result of loading one untrusted image through the helper process (FR-PACK-10, FR-ART-03).</summary>
/// <param name="Status">Outcome.</param>
/// <param name="UserMessage">A sentence the UI can show to the user.</param>
/// <param name="PngBytes">The clean PNG when <paramref name="Status"/> is <see cref="SafeImageStatus.Ok"/>; otherwise null.</param>
/// <param name="Width">Pixel width of <paramref name="PngBytes"/>, or 0.</param>
/// <param name="Height">Pixel height of <paramref name="PngBytes"/>, or 0.</param>
/// <param name="Detail">Technical detail for the log; never shown to the user.</param>
public sealed record SafeImageResult(SafeImageStatus Status, string UserMessage, byte[]? PngBytes, int Width, int Height, string? Detail = null)
{
    /// <summary>The exit codes the image helper documents.</summary>
    public const int ExitOk = 0;

    /// <summary>Exit code: bad input.</summary>
    public const int ExitBadInput = 2;

    /// <summary>Exit code: too large.</summary>
    public const int ExitTooLarge = 3;

    /// <summary>Exit code: decode failed.</summary>
    public const int ExitDecodeFailed = 4;

    /// <summary>Exit code: the helper's own watchdog stopped it (working set or run time over the limit).</summary>
    public const int ExitResourceLimit = 5;

    /// <summary>Exit code (fit mode only): the fitted PNG is 2 MB or more, so no output was written.</summary>
    public const int ExitOutputTooLarge = 6;

    /// <summary>A success carrying the clean PNG.</summary>
    public static SafeImageResult Success(byte[] png, int width, int height) =>
        new(SafeImageStatus.Ok, "Image loaded.", png, width, height);

    /// <summary>A failure with the standard message for its status.</summary>
    public static SafeImageResult Failure(SafeImageStatus status, string? detail = null) => new(status, MessageFor(status), null, 0, 0, detail);

    /// <summary>Maps a non-zero helper exit code to a failure. Unknown codes are treated as a failed decode.</summary>
    public static SafeImageResult FromExitCode(int exitCode) => exitCode switch
    {
        ExitBadInput => Failure(SafeImageStatus.BadInput),
        ExitTooLarge => Failure(SafeImageStatus.TooLarge),
        ExitDecodeFailed => Failure(SafeImageStatus.DecodeFailed),
        ExitResourceLimit => Failure(SafeImageStatus.ResourceLimit),
        ExitOutputTooLarge => Failure(SafeImageStatus.OutputTooLarge),
        _ => Failure(SafeImageStatus.DecodeFailed, $"unexpected helper exit code {exitCode}"),
    };

    /// <summary>The standard user-facing sentence for a status.</summary>
    public static string MessageFor(SafeImageStatus status) => status switch
    {
        SafeImageStatus.Ok => "Image loaded.",
        SafeImageStatus.BadInput => "That file is not a PNG, JPEG or WebP image, or it could not be read.",
        SafeImageStatus.TooLarge => "That image is too large. Use a file under 8 MB and no bigger than 4096 by 4096 pixels.",
        SafeImageStatus.DecodeFailed => "That image could not be opened. It may be damaged or use an unsupported variant.",
        SafeImageStatus.TimedOut => "Opening that image took too long and was stopped. Try a smaller or simpler file.",
        SafeImageStatus.HelperUnavailable => "The image helper could not be started. Reinstall the app or restore its files.",
        SafeImageStatus.ResourceLimit => "That image needed too much memory or time to open and was stopped. Try a smaller or simpler file.",
        SafeImageStatus.OutputTooLarge => "That picture is still 2 MB or more after it was made smaller. Try a simpler picture.",
        _ => "The image could not be loaded.",
    };
}

/// <summary>
/// Loads untrusted images without ever decoding them in the app process: a separate helper process validates, decodes
/// and re-encodes them, and only the clean PNG comes back (FR-PACK-10).
/// </summary>
public interface ISafeImageLoader
{
    /// <summary>Loads an image file the user picked or the cache holds.</summary>
    Task<SafeImageResult> LoadFileAsync(string path, CancellationToken cancellationToken);

    /// <summary>Loads image bytes held in memory, such as an entry read from an import archive.</summary>
    Task<SafeImageResult> LoadBytesAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
}

/// <summary>Reads the size of a PNG from its header so the app can keep a dimension cap on the helper's output.</summary>
public static class PngHeader
{
    private static readonly byte[] s_magic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Returns true and the pixel size when <paramref name="data"/> starts with a PNG signature and IHDR chunk.</summary>
    public static bool TryReadSize(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (data.Length < 24 || !data.StartsWith(s_magic) || !data.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return false;
        }

        var w = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16, 4));
        var h = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20, 4));
        if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue)
        {
            return false;
        }

        width = (int)w;
        height = (int)h;
        return true;
    }
}
