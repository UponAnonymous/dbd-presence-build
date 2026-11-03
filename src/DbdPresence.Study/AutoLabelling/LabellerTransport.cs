namespace DbdPresence.Study;

/// <summary>How one labelling request ended, reduced to what the labelling run acts on.</summary>
internal enum LabellerOutcome
{
    /// <summary>The labeller answered; see the text and the stop reason.</summary>
    Ok,

    /// <summary>The labeller is not set up on the service.</summary>
    NotSetUp,

    /// <summary>This account may not label (signed out, no licence, or not the owner's own account).</summary>
    NotAllowed,

    /// <summary>The labeller is busy; try again after the wait.</summary>
    Busy,

    /// <summary>The day's allowance of requests is used up.</summary>
    DailyCapReached,

    /// <summary>The labeller refused the request or its answer could not be read.</summary>
    Refused,

    /// <summary>The request did not complete (offline, timeout, or the body was refused).</summary>
    Failed,

    /// <summary>The run reached its own limit of requests and stopped (it never comes from the relay).</summary>
    RunLimitReached,
}

/// <summary>An answer to one labelling request.</summary>
/// <param name="Outcome">How the request ended.</param>
/// <param name="Text">The answer text (a JSON document) when <see cref="LabellerOutcome.Ok"/>.</param>
/// <param name="StopReason">Why the answer ended; only <c>end_turn</c> is a complete answer.</param>
/// <param name="InputTokens">Tokens read.</param>
/// <param name="OutputTokens">Tokens written.</param>
/// <param name="RetryAfter">The wait the service asked for, or null.</param>
/// <param name="Error">The service's error code, or null.</param>
internal sealed record LabellerReply(LabellerOutcome Outcome, string Text, string StopReason, int InputTokens, int OutputTokens, TimeSpan? RetryAfter, string? Error)
{
    /// <summary>Makes a reply that carries no answer.</summary>
    public static LabellerReply Without(LabellerOutcome outcome, TimeSpan? retryAfter = null, string? error = null) =>
        new(outcome, string.Empty, string.Empty, 0, 0, retryAfter, error);
}

/// <summary>Sends one request body to the labelling relay and returns its answer. The real one is in the app; tests use a fake.</summary>
internal interface ILabellerTransport
{
    /// <summary>Sends a whole request body.</summary>
    /// <param name="body">The JSON body.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The answer.</returns>
    Task<LabellerReply> SendAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken);
}
