using System.Globalization;

namespace DbdPresence.Core.Pictures;

/// <summary>Why a link was refused.</summary>
public enum LinkProblem
{
    /// <summary>The link is acceptable.</summary>
    None,

    /// <summary>The link is empty or not an absolute address.</summary>
    NotALink,

    /// <summary>The link does not start with https://.</summary>
    NotHttps,

    /// <summary>The link is longer than 512 characters.</summary>
    TooLong,

    /// <summary>The link holds a user name or password.</summary>
    HasCredentials,

    /// <summary>The host is an IP address.</summary>
    IpAddress,

    /// <summary>The host is local: <c>localhost</c>, a <c>.local</c> name or a single-label name.</summary>
    LocalHost,

    /// <summary>The host is one of the owner's own hosts, which the app already serves.</summary>
    OwnerHost,

    /// <summary>The link holds a control character or whitespace.</summary>
    BadCharacters,
}

/// <summary>The outcome of checking a link.</summary>
/// <param name="Problem">What is wrong, or <see cref="LinkProblem.None"/>.</param>
/// <param name="Normalised">The link to store (scheme and host lower-case) when it is acceptable.</param>
public sealed record LinkCheck(LinkProblem Problem, string? Normalised)
{
    /// <summary>True when the link is acceptable.</summary>
    public bool IsValid => Problem == LinkProblem.None;

    /// <summary>The sentence for the user (PLAN_V2_S5B §7.5 link errors), or null when valid.</summary>
    public string? Message => Problem switch
    {
        LinkProblem.None => null,
        LinkProblem.TooLong => "This link is too long (512 characters at most).",
        LinkProblem.HasCredentials => "Links can't contain a user name or password.",
        LinkProblem.IpAddress => "Use a site name, not an IP address.",
        LinkProblem.LocalHost => "Use a link to a public site, not a local address.",
        LinkProblem.OwnerHost => "Use a link to another site. Pictures from this app's own site are added from a pack.",
        LinkProblem.BadCharacters => "A link can't contain spaces or control characters.",
        _ => "Use a link that starts with https://.",
    };
}

/// <summary>
/// The rules for a picture link (PLAN_V2 §2.5, PLAN_V2_S5B §2.6): https only, at most 512 characters, no credentials, no IP address, and not
/// the owner's own hosts, <c>localhost</c>, <c>.local</c> or a single-label name. The app never fetches a link (FR-PACK-14); this class only
/// reads the text.
/// </summary>
public static class LinkPolicy
{
    private const string OwnerDomain = "arcavale.com";

    /// <summary>Checks <paramref name="text"/>.</summary>
    public static LinkCheck Check(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return new LinkCheck(LinkProblem.NotALink, null);
        }

        if (trimmed.Length > PictureLimits.MaxLinkLength)
        {
            return new LinkCheck(LinkProblem.TooLong, null);
        }

        if (trimmed.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            return new LinkCheck(LinkProblem.BadCharacters, null);
        }

        if (!trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return new LinkCheck(LinkProblem.NotHttps, null);
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return new LinkCheck(LinkProblem.NotALink, null);
        }

        if (uri.UserInfo.Length > 0)
        {
            return new LinkCheck(LinkProblem.HasCredentials, null);
        }

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0)
        {
            return new LinkCheck(LinkProblem.NotALink, null);
        }

        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 || IsIpLike(host))
        {
            return new LinkCheck(LinkProblem.IpAddress, null);
        }

        if (host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal) || host.EndsWith(".local", StringComparison.Ordinal)
            || !host.Contains('.', StringComparison.Ordinal))
        {
            return new LinkCheck(LinkProblem.LocalHost, null);
        }

        if (host == OwnerDomain || host.EndsWith("." + OwnerDomain, StringComparison.Ordinal))
        {
            return new LinkCheck(LinkProblem.OwnerHost, null);
        }

        return new LinkCheck(LinkProblem.None, uri.AbsoluteUri);
    }

    // No real top-level domain is all digits or hexadecimal, so a last label like that is an IPv4 address in some notation ("127.0.0.1",
    // "2130706433", "0x7f.1"). The text is only read here; no network type is used.
    private static bool IsIpLike(string host)
    {
        if (host.Contains(':', StringComparison.Ordinal))
        {
            return true;
        }

        var last = host[(host.LastIndexOf('.') + 1)..];
        return last.Length > 0 && (last.All(char.IsAsciiDigit)
            || (last.StartsWith("0x", StringComparison.Ordinal) && int.TryParse(last.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)));
    }
}
