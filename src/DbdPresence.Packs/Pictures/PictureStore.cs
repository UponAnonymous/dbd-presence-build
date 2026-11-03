using System.Security.Cryptography;
using DbdPresence.Core.Images;
using DbdPresence.Core.Pictures;
using DbdPresence.Packs.Local;

namespace DbdPresence.Packs.Pictures;

/// <summary>The outcome of making an own picture from a file or from pack bytes.</summary>
/// <param name="Item">The picture, saved on this PC; null on failure.</param>
/// <param name="Error">What went wrong, in plain words, when <paramref name="Item"/> is null.</param>
/// <param name="Notice">A sentence to show next to a success (for example that the picture was made smaller), or null.</param>
public sealed record PictureIntakeResult(PictureItem? Item, string? Error, string? Notice = null);

/// <summary>
/// Saves the user's own pictures as content-addressed PNG files under <c>packs/pictures/&lt;sha256&gt;.png</c> (PLAN_V2_S5B §2.6, FR-PACK-12).
/// Nothing is decoded here: the image helper makes every picture smaller through <see cref="IPictureFitter"/> (ADR-0013), and this class checks
/// what comes back. Every saved file is under 2 MB: a file of 2 MB or more at 1024 pixels is redone at 512 with a notice, and a file that is
/// still too big is refused. Files that older versions kept under <c>packs/overrides/</c> are found as well. Nothing here uses the network.
/// </summary>
public sealed class PictureStore
{
    private const long MaxSourceBytes = 8L * 1024 * 1024;

    private readonly ILocalPackStorage _storage;
    private readonly IPictureFitter _fitter;
    private readonly TimeProvider _time;
    private readonly string _picturesRoot;
    private readonly string _legacyRoot;
    private readonly Func<string, bool> _isLocalPath;

    /// <summary>Initializes a new instance of the <see cref="PictureStore"/> class.</summary>
    /// <param name="storage">File operations.</param>
    /// <param name="packsRoot">The <c>packs</c> folder under the app data folder.</param>
    /// <param name="fitter">Makes images smaller in the image helper process.</param>
    /// <param name="time">The clock stamped on new pictures.</param>
    /// <param name="isLocalPath">Decides whether a path the user chose may be opened at all (network and device paths never are); the default is <see cref="LocalPathGuard.IsAcceptable"/>.</param>
    public PictureStore(ILocalPackStorage storage, string packsRoot, IPictureFitter fitter, TimeProvider time, Func<string, bool>? isLocalPath = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentException.ThrowIfNullOrEmpty(packsRoot);
        ArgumentNullException.ThrowIfNull(fitter);
        ArgumentNullException.ThrowIfNull(time);
        _storage = storage;
        _fitter = fitter;
        _time = time;
        _isLocalPath = isLocalPath ?? LocalPathGuard.IsAcceptable;
        _picturesRoot = Path.Combine(packsRoot, "pictures");
        _legacyRoot = Path.Combine(packsRoot, "overrides");
    }

    /// <summary>The stored file of an own picture, or null for a link, a bad name or a missing file.</summary>
    public string? PathOf(PictureItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.File is null || !IsFileName(item.File))
        {
            return null;
        }

        foreach (var root in new[] { _picturesRoot, _legacyRoot })
        {
            var path = Path.Combine(root, item.File);
            if (_storage.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>The size of an own picture's file: the recorded size, or the file's length when the record has none (migrated pictures). 0 for a link or a missing file.</summary>
    public long BytesOf(PictureItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Bytes > 0)
        {
            return item.Bytes;
        }

        return PathOf(item) is { } path ? _storage.Length(path) : 0;
    }

    /// <summary>Makes a picture from a file the user chose.</summary>
    /// <param name="path">The local file path.</param>
    /// <param name="size">The longest side to save: 512 or 1024.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    public async Task<PictureIntakeResult> AddFileAsync(string path, int size, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!_isLocalPath(path))
        {
            // Nothing is opened, not even to hash it: a network path would make Windows reach out to another computer (ADR-0016).
            return new PictureIntakeResult(null, LocalPathGuard.RefusalMessage);
        }

        // The file is read and hashed on a worker thread: up to 8 MB must not stall the window that asked for it.
        var sourceHash = await Task.Run(() => SourceHashOfFile(path), cancellationToken).ConfigureAwait(false);
        return await FitAndSaveAsync(edge => _fitter.FitFileAsync(path, edge, cancellationToken), sourceHash, size, PictureOrigin.Pc)
            .ConfigureAwait(false);
    }

    /// <summary>Makes a picture from an image of an imported pack (bytes already read from the archive).</summary>
    /// <param name="data">The image bytes.</param>
    /// <param name="size">The longest side to save: 512 or 1024.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    public async Task<PictureIntakeResult> AddBytesAsync(ReadOnlyMemory<byte> data, int size, CancellationToken cancellationToken)
    {
        var source = Convert.ToHexStringLower(SHA256.HashData(data.Span));
        return await FitAndSaveAsync(edge => _fitter.FitBytesAsync(data, edge, cancellationToken), source, size, PictureOrigin.Import).ConfigureAwait(false);
    }

    /// <summary>Makes a link picture. Nothing is stored; the link is checked by <see cref="LinkPolicy"/> and never fetched.</summary>
    public PictureIntakeResult AddLink(string? link)
    {
        var check = LinkPolicy.Check(link);
        return PictureSlots.LinkItem(link, _time.GetUtcNow()) is { } item
            ? new PictureIntakeResult(item, null)
            : new PictureIntakeResult(null, check.Message);
    }

    /// <summary>Deletes the stored file of a picture that no character uses any more. Does nothing for a link.</summary>
    public void Delete(PictureItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.File is { } file && IsFileName(file))
        {
            _storage.Delete(Path.Combine(_picturesRoot, file));
            _storage.Delete(Path.Combine(_legacyRoot, file));
        }
    }

    /// <summary>Reads the stored file of an own picture, or null when it is missing or unreadable.</summary>
    public byte[]? Read(PictureItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (PathOf(item) is not { } path || _storage.Length(path) >= PictureLimits.MaxFileBytes)
        {
            return null;
        }

        try
        {
            var bytes = _storage.ReadAllBytes(path);

            // The name is the content's hash: a file that no longer hashes to it was changed on disk and is not the picture that was saved.
            return string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), item.Sha256, StringComparison.Ordinal) ? bytes : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Copies the file of an imported pack's picture into the pictures folder (settings v3 migration). Returns the picture, or null when the source
    /// is missing, unreadable, not under 2 MB or not a plain PNG that <see cref="StrictPng"/> accepts.
    /// </summary>
    /// <param name="png">The imported pack's stored PNG (already made clean by the image helper).</param>
    public PictureItem? Adopt(ReadOnlySpan<byte> png)
    {
        if (!PictureLimits.FitsFileLimit(png.Length) || !StrictPng.TryValidate(png, out _, out _, out _))
        {
            return null;
        }

        var sha = Convert.ToHexStringLower(SHA256.HashData(png));
        return TryWrite(sha, png) ? new PictureItem(sha, null, png.Length, sha + ".png", null, PictureOrigin.Import, _time.GetUtcNow()) : null;
    }

    // The hash of the file as the user had it, to match an owner copy later. Unknown (null) when the file cannot be read here; the helper still reads it.
    private string? SourceHashOfFile(string path)
    {
        try
        {
            return _storage.Exists(path) && _storage.Length(path) <= MaxSourceBytes
                ? Convert.ToHexStringLower(SHA256.HashData(_storage.ReadAllBytes(path)))
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Copies a stored picture file of an imported pack (settings v3 migration). Null when it is missing, unreadable or not under 2 MB.</summary>
    /// <param name="path">The imported pack's file.</param>
    public PictureItem? AdoptFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!_isLocalPath(path))
        {
            return null;
        }

        try
        {
            return _storage.Exists(path) && PictureLimits.FitsFileLimit(_storage.Length(path)) ? Adopt(_storage.ReadAllBytes(path)) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsFileName(string name) =>
        name.Length == 68 && name.EndsWith(".png", StringComparison.Ordinal) && PictureKeys.IsSha256(name[..64]);

    private async Task<PictureIntakeResult> FitAndSaveAsync(
        Func<int, Task<SafeImageResult>> fit, string? sourceSha256, int size, PictureOrigin origin)
    {
        if (!PictureLimits.IsSize(size))
        {
            size = PictureLimits.DefaultSize;
        }

        string? notice = null;
        var result = await fit(size).ConfigureAwait(false);
        var tooBigAtLargeSize = result.Status == SafeImageStatus.OutputTooLarge
            || (result.Status == SafeImageStatus.Ok && result.PngBytes is { } first && first.Length >= PictureLimits.MaxFileBytes);
        if (tooBigAtLargeSize && size > PictureLimits.DefaultSize)
        {
            notice = "This picture was 2 MB or more at the larger size, so it was made smaller (512 × 512).";
            size = PictureLimits.DefaultSize;
            result = await fit(size).ConfigureAwait(false);
        }

        if (result.Status != SafeImageStatus.Ok || result.PngBytes is not { } png)
        {
            return new PictureIntakeResult(null, result.UserMessage);
        }

        if (!PngHeader.TryReadSize(png, out var width, out var height) || Math.Max(width, height) > size)
        {
            return new PictureIntakeResult(null, "That picture could not be made smaller. Try another file.");
        }

        if (!PictureLimits.FitsFileLimit(png.Length))
        {
            return new PictureIntakeResult(null, "That picture is still 2 MB or more after it was made smaller. Try a simpler picture.");
        }

        var sha = Convert.ToHexStringLower(SHA256.HashData(png));
        if (!TryWrite(sha, png))
        {
            return new PictureIntakeResult(null, "The picture could not be saved. Check free disk space and that the app data folder is writable.");
        }

        return new PictureIntakeResult(new PictureItem(sha, sourceSha256, png.Length, sha + ".png", null, origin, _time.GetUtcNow()), null, notice);
    }

    private bool TryWrite(string sha, ReadOnlySpan<byte> png)
    {
        var target = Path.Combine(_picturesRoot, sha + ".png");
        var temp = $"{target}.{Guid.NewGuid():N}.tmp";
        try
        {
            if (!_storage.Exists(target) || !FileHashes(target, sha))
            {
                // A file that is missing, or whose content is not what its name says (changed on disk), is written again.
                _storage.CreateDirectory(_picturesRoot);
                _storage.WriteAllBytesDurable(temp, png);
                _storage.Move(temp, target);
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            _ = TryDelete(temp);
        }
    }

    private bool FileHashes(string path, string sha)
    {
        try
        {
            return _storage.Length(path) <= PictureLimits.MaxFileBytes
                && string.Equals(Convert.ToHexStringLower(SHA256.HashData(_storage.ReadAllBytes(path))), sha, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    // A temp file left by a failed move is removed. False when it could not be removed: it is then only a stray .tmp that nothing reads.
    private bool TryDelete(string path)
    {
        try
        {
            if (_storage.Exists(path))
            {
                _storage.Delete(path);
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
