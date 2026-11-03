namespace DbdPresence.Discord.Ipc;

/// <summary>What happened to a <c>SET_ACTIVITY</c> request.</summary>
internal enum SetActivityOutcome
{
    /// <summary>Discord acknowledged the update.</summary>
    Success,

    /// <summary>Discord answered with an error for this request; the connection is still usable.</summary>
    DiscordError,

    /// <summary>The connection ended before an answer arrived.</summary>
    Disconnected,

    /// <summary>No answer arrived within the ack timeout; the connection has been torn down.</summary>
    Timeout,
}

/// <summary>Typed result of <see cref="DiscordIpcClient.SetActivityAsync"/>.</summary>
/// <param name="Outcome">The outcome.</param>
/// <param name="Code">The Discord error code for <see cref="SetActivityOutcome.DiscordError"/>.</param>
/// <param name="Message">A human-readable description for every outcome except success.</param>
internal readonly record struct SetActivityResult(SetActivityOutcome Outcome, int? Code, string? Message)
{
    /// <summary>Discord acknowledged the update.</summary>
    public static SetActivityResult Success { get; } = new(SetActivityOutcome.Success, null, null);

    /// <summary>Discord rejected the update.</summary>
    public static SetActivityResult DiscordError(int? code, string? message) => new(SetActivityOutcome.DiscordError, code, message);

    /// <summary>The connection ended.</summary>
    public static SetActivityResult Disconnected(string message) => new(SetActivityOutcome.Disconnected, null, message);

    /// <summary>No acknowledgement arrived in time.</summary>
    public static SetActivityResult Timeout(string message) => new(SetActivityOutcome.Timeout, null, message);
}
