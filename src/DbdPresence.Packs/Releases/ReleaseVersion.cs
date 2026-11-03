using System.Globalization;

namespace DbdPresence.Packs.Releases;

/// <summary>
/// An app version as the signed release feed carries it (FR-ONL-10): <c>major.minor.patch</c> with an optional pre-release label of lower-case
/// letters, digits, hyphens and dots, for example <c>1.4.0</c> or <c>1.5.0-beta.2</c>. Build metadata (<c>+…</c>) and upper case are refused, so
/// the app, Velopack and the service can never order two versions differently. Ordering is SemVer 2.0 precedence.
/// </summary>
public sealed class ReleaseVersion : IComparable<ReleaseVersion>, IEquatable<ReleaseVersion>
{
    /// <summary>The longest accepted text.</summary>
    public const int MaxLength = 64;

    private const int MaxPart = 999_999_999;

    private readonly string[] _label;

    private ReleaseVersion(int major, int minor, int patch, string[] label, string text)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _label = label;
        Text = text;
    }

    /// <summary>Gets the major part.</summary>
    public int Major { get; }

    /// <summary>Gets the minor part.</summary>
    public int Minor { get; }

    /// <summary>Gets the patch part.</summary>
    public int Patch { get; }

    /// <summary>Gets a value indicating whether the version has a pre-release label (a beta).</summary>
    public bool IsPrerelease => _label.Length > 0;

    /// <summary>Gets the canonical text, exactly as parsed.</summary>
    public string Text { get; }

    /// <summary>Parses a version; returns null for anything that is not in the accepted form.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The version, or null.</returns>
    public static ReleaseVersion? TryParse(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength)
        {
            return null;
        }

        var dash = text.IndexOf('-', StringComparison.Ordinal);
        var core = dash < 0 ? text : text[..dash];
        var parts = core.Split('.');
        if (parts.Length != 3 || !TryPart(parts[0], out var major) || !TryPart(parts[1], out var minor) || !TryPart(parts[2], out var patch))
        {
            return null;
        }

        string[] label = [];
        if (dash >= 0)
        {
            label = text[(dash + 1)..].Split('.');
            if (label.Any(identifier => !IsIdentifier(identifier)))
            {
                return null;
            }
        }

        return new ReleaseVersion(major, minor, patch, label, text);
    }

    /// <summary>Parses a version or throws.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The version.</returns>
    /// <exception cref="FormatException">The text is not an accepted version.</exception>
    public static ReleaseVersion Parse(string text) => TryParse(text) ?? throw new FormatException($"'{text}' is not a release version (major.minor.patch, optional lower-case pre-release label).");

    /// <inheritdoc />
    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var core = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (core != 0)
        {
            return core;
        }

        // A version without a label is higher than any pre-release of it (SemVer 2.0 §11).
        if (_label.Length == 0 || other._label.Length == 0)
        {
            if (_label.Length == other._label.Length)
            {
                return 0;
            }

            return _label.Length == 0 ? 1 : -1;
        }

        for (var i = 0; i < Math.Min(_label.Length, other._label.Length); i++)
        {
            var compared = CompareIdentifier(_label[i], other._label[i]);
            if (compared != 0)
            {
                return compared;
            }
        }

        return _label.Length.CompareTo(other._label.Length);
    }

    /// <inheritdoc />
    public bool Equals(ReleaseVersion? other) => other is not null && CompareTo(other) == 0;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ReleaseVersion other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Text);

    /// <inheritdoc />
    public override string ToString() => Text;

    /// <summary>Compares two versions; null is lowest.</summary>
    public static bool operator <(ReleaseVersion? left, ReleaseVersion? right) => Compare(left, right) < 0;

    /// <summary>Compares two versions; null is lowest.</summary>
    public static bool operator >(ReleaseVersion? left, ReleaseVersion? right) => Compare(left, right) > 0;

    /// <summary>Compares two versions; null is lowest.</summary>
    public static bool operator <=(ReleaseVersion? left, ReleaseVersion? right) => Compare(left, right) <= 0;

    /// <summary>Compares two versions; null is lowest.</summary>
    public static bool operator >=(ReleaseVersion? left, ReleaseVersion? right) => Compare(left, right) >= 0;

    /// <summary>Equality by precedence.</summary>
    public static bool operator ==(ReleaseVersion? left, ReleaseVersion? right) => Compare(left, right) == 0;

    /// <summary>Inequality by precedence.</summary>
    public static bool operator !=(ReleaseVersion? left, ReleaseVersion? right) => Compare(left, right) != 0;

    private static int Compare(ReleaseVersion? left, ReleaseVersion? right) => left is null ? (right is null ? 0 : -1) : left.CompareTo(right);

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = left.All(char.IsAsciiDigit);
        var rightNumeric = right.All(char.IsAsciiDigit);
        if (leftNumeric && rightNumeric)
        {
            return left.Length != right.Length ? left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right);
        }

        // Numeric identifiers are lower than alphanumeric ones (SemVer 2.0 §11.4.3).
        if (leftNumeric != rightNumeric)
        {
            return leftNumeric ? -1 : 1;
        }

        return Math.Sign(string.CompareOrdinal(left, right));
    }

    private static bool TryPart(string text, out int value)
    {
        value = 0;
        return text.Length is > 0 and <= 9
            && text.All(char.IsAsciiDigit)
            && (text.Length == 1 || text[0] != '0')
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value <= MaxPart;
    }

    private static bool IsIdentifier(string identifier) =>
        identifier.Length > 0
        && identifier.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')
        && !(identifier.Length > 1 && identifier[0] == '0' && identifier.All(char.IsAsciiDigit));
}
