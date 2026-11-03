namespace DbdPresence.ImageHelper;

/// <summary>The helper's process exit codes. The launcher maps each to a user-facing message.</summary>
internal static class ExitCodes
{
    /// <summary>The output PNG was written.</summary>
    public const int Ok = 0;

    /// <summary>The input is missing, unreadable or not a PNG, JPEG or WebP file (or the arguments are wrong).</summary>
    public const int BadInput = 2;

    /// <summary>The file is over 8 MB or the image is over 4096 by 4096 pixels.</summary>
    public const int TooLarge = 3;

    /// <summary>The file has valid magic bytes but could not be decoded or re-encoded.</summary>
    public const int DecodeFailed = 4;

    /// <summary>The watchdog stopped the helper: working set over 512 MB or run time over 12 seconds.</summary>
    public const int ResourceLimit = 5;

    /// <summary>Fit mode only: the fitted PNG is 2 MB or more, so no output file was written.</summary>
    public const int OutputTooLarge = 6;
}
