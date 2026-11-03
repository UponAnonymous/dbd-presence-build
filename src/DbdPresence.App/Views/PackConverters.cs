using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DbdPresence.Core.Images;
using DbdPresence.Core.Pictures;
using DbdPresence.Packs.Local;

namespace DbdPresence.App.Views;

/// <summary>
/// Turns a pack image location into a picture (FR-PACK-10, FR-ART-06). It only ever shows PNG
/// files that pass <see cref="StrictPng"/> (they were normalised by the image helper before they were stored). It never
/// takes a remote address, so no image is fetched over the network. A file that fails the check shows nothing and is
/// logged by type only.
/// </summary>
public sealed class PackImageConverter : IValueConverter
{
    private const int DecodeWidth = 192;

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not PackImageLocation location)
        {
            return null;
        }

        return File(location.Path);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;

    /// <summary>Decodes a PNG already held in memory, after the strict chunk check. Returns null when it fails.</summary>
    internal static ImageSource? Decode(byte[] png)
    {
        if (!StrictPng.TryValidate(png, out _, out _, out var failure))
        {
            Serilog.Log.Warning("A pack image was not shown because it failed the PNG check: {Reason}", failure);
            return null;
        }

        try
        {
            using var stream = new MemoryStream(png, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = DecodeWidth;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or IOException)
        {
            Serilog.Log.Warning("A pack image could not be shown: {ExceptionType}", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>Shows a local picture file by its path (own pictures and cached pack pictures); never a remote address.</summary>
    internal static ImageSource? FromFile(string path) => File(path);

    private static ImageSource? File(string path)
    {
        try
        {
            // Every saved picture is under 2 MB; a larger file is not one of ours and is not read (security review L3).
            if (BoundedFile.ReadAllBytes(path, PictureLimits.MaxFileBytes) is not { } bytes)
            {
                Serilog.Log.Warning("A pack image file was not shown because it is larger than {Limit} bytes", PictureLimits.MaxFileBytes);
                return null;
            }

            return Decode(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning("A pack image file could not be read: {ExceptionType}", ex.GetType().Name);
            return null;
        }
    }
}

/// <summary>Shows a clean PNG that the image helper returned during an import (checked with <see cref="StrictPng"/> first).</summary>
public sealed class PngBytesConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is byte[] png ? PackImageConverter.Decode(png) : null;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// Shows a local picture by its file path (the Characters page: tiles, previews, options). A path that is not a file on this PC, or a file that fails the
/// strict PNG check, shows nothing; a web address is never loaded (the UI loads no remote images).
/// </summary>
public sealed class PicturePathConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } path && Path.IsPathFullyQualified(path) && !path.StartsWith("http", StringComparison.OrdinalIgnoreCase) && LocalPathGuard.IsAcceptable(path)
            ? PackImageConverter.FromFile(path)
            : null;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
