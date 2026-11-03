using System.Globalization;
using System.Text.Json;
using DbdPresence.Core.Detection;
using DbdPresence.Vision.Imaging;
using DbdPresence.Vision.Matching;

namespace DbdPresence.Vision.Screens;

/// <summary>
/// The pictures of the own-row state icons (portrait, hooked, dying, dead) the owner's own captures taught this PC (ADR-0004, FR-AUTO-03). They are
/// small grey crops of the icon cell, learned locally and kept on this PC only; nothing here ships game artwork, and a pack picture is never used as
/// a template. With none, the own-status detector has nothing to compare with and stays silent. The set is bounded and validated: a class outside
/// <see cref="OwnStatuses"/>, a flat picture (no contrast) or an oversized one is refused.
/// </summary>
public sealed class OwnStatusTemplates
{
    /// <summary>The most pictures kept per class.</summary>
    public const int MaxPerClass = 8;

    /// <summary>The longest side of a template in pixels.</summary>
    public const int MaxSide = 128;

    /// <summary>The newest file format.</summary>
    public const int FormatVersion = 1;

    private readonly Dictionary<string, IReadOnlyList<ImageBuffer>> _byClass;

    /// <summary>Initializes a new instance of the <see cref="OwnStatusTemplates"/> class.</summary>
    /// <param name="byClass">The pictures by class.</param>
    public OwnStatusTemplates(IReadOnlyDictionary<string, IReadOnlyList<ImageBuffer>> byClass)
    {
        ArgumentNullException.ThrowIfNull(byClass);
        _byClass = new Dictionary<string, IReadOnlyList<ImageBuffer>>(StringComparer.Ordinal);
        foreach (var (name, pictures) in byClass)
        {
            if (!OwnStatuses.IsValid(name))
            {
                throw new ArgumentException($"'{name}' is not an own-status class.", nameof(byClass));
            }

            if (pictures.Count > MaxPerClass)
            {
                throw new ArgumentException($"At most {MaxPerClass} templates per class.", nameof(byClass));
            }

            var kept = new List<ImageBuffer>(pictures.Count);
            foreach (var picture in pictures)
            {
                var problem = ProblemWith(picture);
                if (problem is not null)
                {
                    throw new ArgumentException($"A template for '{name}' {problem}.", nameof(byClass));
                }

                kept.Add(picture.ToGray());
            }

            if (kept.Count > 0)
            {
                _byClass[name] = kept;
            }
        }
    }

    /// <summary>Gets the set with no pictures: the detector reads nothing.</summary>
    public static OwnStatusTemplates Empty { get; } = new(new Dictionary<string, IReadOnlyList<ImageBuffer>>());

    /// <summary>Gets a value indicating whether there are no pictures at all.</summary>
    public bool IsEmpty => _byClass.Count == 0;

    /// <summary>
    /// Gets a value indicating whether every class has a picture. The detector reads only then: without a picture of the portrait and of the dead icon, a
    /// hooked icon could not be told from them, so an incomplete set fails closed.
    /// </summary>
    public bool IsComplete => _byClass.ContainsKey(OwnStatuses.Portrait) && _byClass.ContainsKey(OwnStatuses.Hooked)
        && _byClass.ContainsKey(OwnStatuses.Dying) && _byClass.ContainsKey(OwnStatuses.Dead);

    /// <summary>Gets the classes that have at least one picture.</summary>
    public IReadOnlyCollection<string> Classes => _byClass.Keys;

    /// <summary>Returns the pictures of a class (empty when none).</summary>
    /// <param name="name">The class.</param>
    public IReadOnlyList<ImageBuffer> For(string name) => _byClass.TryGetValue(name, out var list) ? list : [];

    /// <summary>Reads a set from its JSON file. Returns null and a reason when the file is not usable, so the caller can log it and go on without templates.</summary>
    /// <param name="json">The file's text.</param>
    /// <param name="problem">Why the file was refused, or null.</param>
    public static OwnStatusTemplates? TryParse(string json, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != FormatVersion
                || !root.TryGetProperty("templates", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                problem = "the file is not a status template file of this version";
                return null;
            }

            var byClass = new Dictionary<string, List<ImageBuffer>>(StringComparer.Ordinal);
            foreach (var item in list.EnumerateArray())
            {
                var name = item.GetProperty("class").GetString() ?? string.Empty;
                var width = item.GetProperty("width").GetInt32();
                var height = item.GetProperty("height").GetInt32();
                var gray = Convert.FromBase64String(item.GetProperty("gray").GetString() ?? string.Empty);
                if (width is < 1 or > MaxSide || height is < 1 or > MaxSide || gray.Length != width * height)
                {
                    problem = "a template has a wrong size";
                    return null;
                }

                if (!byClass.TryGetValue(name, out var pictures))
                {
                    pictures = [];
                    byClass[name] = pictures;
                }

                pictures.Add(new ImageBuffer(width, height, PixelFormat.Gray8, gray));
            }

            problem = null;
            return new OwnStatusTemplates(byClass.ToDictionary(p => p.Key, p => (IReadOnlyList<ImageBuffer>)p.Value, StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException or OverflowException)
        {
            problem = "the file could not be read: " + ex.GetType().Name;
            return null;
        }
    }

    /// <summary>Writes the set as the JSON file the app loads at start.</summary>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", FormatVersion);
            writer.WriteStartArray("templates");
            foreach (var name in _byClass.Keys.Order(StringComparer.Ordinal))
            {
                foreach (var picture in _byClass[name])
                {
                    writer.WriteStartObject();
                    writer.WriteString("class", name);
                    writer.WriteNumber("width", picture.Width);
                    writer.WriteNumber("height", picture.Height);
                    writer.WriteString("gray", Convert.ToBase64String(picture.Pixels));
                    writer.WriteEndObject();
                }
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Scores a crop of the icon cell against every picture: the zero-mean normalised correlation of the crop, resized to the picture's size, with the
    /// picture. The result is the best score per class (classes with no picture are absent). Correlation ignores brightness and contrast, which is what
    /// lets a picture learned in one lighting condition match in another.
    /// </summary>
    /// <param name="crop">The crop of the icon cell.</param>
    public IReadOnlyDictionary<string, double> Score(ImageBuffer crop)
    {
        ArgumentNullException.ThrowIfNull(crop);
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (name, pictures) in _byClass)
        {
            var best = double.NegativeInfinity;
            foreach (var picture in pictures)
            {
                var fitted = crop.Width == picture.Width && crop.Height == picture.Height ? crop : crop.Resize(picture.Width, picture.Height);
                if (TemplateMatcher.Match(fitted, picture) is { } match)
                {
                    best = Math.Max(best, match.Score);
                }
            }

            if (!double.IsNegativeInfinity(best))
            {
                scores[name] = best;
            }
        }

        return scores;
    }

    private static string? ProblemWith(ImageBuffer picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        if (picture.Width is < TemplateMatcher.MinimumTemplateSide or > MaxSide || picture.Height is < TemplateMatcher.MinimumTemplateSide or > MaxSide)
        {
            return string.Create(CultureInfo.InvariantCulture, $"must be {TemplateMatcher.MinimumTemplateSide} to {MaxSide} pixels on each side");
        }

        var gray = picture.ToGray();
        var first = gray.Pixels[0];
        return gray.Pixels.IndexOfAnyExcept(first) < 0 ? "is flat (no contrast)" : null;
    }
}
