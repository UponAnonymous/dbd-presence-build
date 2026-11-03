using System.Globalization;

namespace DbdPresence.Spikes;

internal sealed record SpikeOptions(string AppId, string ImageUrl)
{
    // The repository is private, so a raw GitHub URL returns 404 to Discord's media proxy; Discord's own public default
    // avatar is a stable, publicly reachable PNG.
    public const string DefaultImageUrl = "https://cdn.discordapp.com/embed/avatars/0.png";

    /// <summary>The owner's test application (public ID), used when --app-id is not given so the tool runs on double-click.</summary>
    public const string DefaultAppId = "1563291325107937340";

    public const string Usage =
        "Usage: DbdPresence.Spikes [--app-id <id>] [--image-url <https url>]\n"
        + "       DbdPresence.Spikes --cadence [--intervals 1,2,4,5,10] [--updates 6] [--rest 20] [--app-id <id>]";

    /// <summary>The update gaps, in seconds, that the cadence mode tries when --intervals is not given.</summary>
    public static readonly IReadOnlyList<double> DefaultIntervalsSeconds = [1, 2, 4, 5, 10];

    /// <summary>Gets a value indicating whether to run the update-cadence mode (--cadence) instead of the V2-S1 steps.</summary>
    public bool Cadence { get; init; }

    /// <summary>Gets the gaps between updates, one block per value (cadence mode).</summary>
    public IReadOnlyList<double> IntervalsSeconds { get; init; } = DefaultIntervalsSeconds;

    /// <summary>Gets how many updates each block sends (cadence mode).</summary>
    public int UpdatesPerBlock { get; init; } = 6;

    /// <summary>Gets the seconds of quiet between blocks, so one block's updates do not count against the next (cadence mode).</summary>
    public int RestSeconds { get; init; } = 20;

    /// <summary>Parses the arguments; returns null and an error text when they are unusable.</summary>
    public static SpikeOptions? Parse(string[] args, out string error)
    {
        string? appId = DefaultAppId;
        var imageUrl = DefaultImageUrl;
        var cadence = false;
        IReadOnlyList<double> intervals = DefaultIntervalsSeconds;
        var updates = 6;
        var rest = 20;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--cadence")
            {
                cadence = true;
                continue;
            }

            if (i + 1 >= args.Length)
            {
                error = $"Missing value after {args[i]}.";
                return null;
            }

            switch (args[i])
            {
                case "--app-id":
                    appId = args[++i];
                    break;
                case "--image-url":
                    imageUrl = args[++i];
                    break;
                case "--intervals":
                    if (!TryParseIntervals(args[++i], out intervals))
                    {
                        error = "--intervals must be 1 to 10 numbers of seconds between 0.5 and 60, separated by commas, for example 1,2,4,5,10.";
                        return null;
                    }

                    break;
                case "--updates":
                    if (!int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out updates) || updates is < 2 or > 30)
                    {
                        error = "--updates must be a whole number from 2 to 30.";
                        return null;
                    }

                    break;
                case "--rest":
                    if (!int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out rest) || rest > 300)
                    {
                        error = "--rest must be a whole number of seconds from 0 to 300.";
                        return null;
                    }

                    break;
                default:
                    error = $"Unknown argument {args[i]}.";
                    return null;
            }
        }

        if (string.IsNullOrWhiteSpace(appId) || !appId.All(char.IsAsciiDigit))
        {
            error = "--app-id must be the numeric Discord application ID.";
            return null;
        }

        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            error = "--image-url must be an absolute https URL.";
            return null;
        }

        error = string.Empty;
        return new SpikeOptions(appId, imageUrl)
        {
            Cadence = cadence,
            IntervalsSeconds = intervals,
            UpdatesPerBlock = updates,
            RestSeconds = rest,
        };
    }

    private static bool TryParseIntervals(string text, out IReadOnlyList<double> intervals)
    {
        var parsed = new List<double>();
        foreach (var part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                || !double.IsFinite(seconds) || seconds < 0.5 || seconds > 60)
            {
                intervals = [];
                return false;
            }

            parsed.Add(seconds);
        }

        intervals = parsed;
        return parsed.Count is >= 1 and <= 10;
    }
}
