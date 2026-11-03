namespace DbdPresence.LocalCi;

/// <summary>What a notice is about. Each topic is shown at most once a day.</summary>
public enum NoticeTopic
{
    /// <summary>The token ends soon, or GitHub refuses it.</summary>
    Token,

    /// <summary>A clean-up could not remove a Linux machine or a folder.</summary>
    Residue,

    /// <summary>The repository is not private, so no runner is started (local CI round 3, red-team M5).</summary>
    PublicRepository,

    /// <summary>A job was stopped because the drive that holds the Linux machines was almost full (red-team M1).</summary>
    DiskFull,

    /// <summary>The isolation probe no longer allows Linux checks on this PC (WSL or the setup changed since it was accepted, or the program's own repeat of it found a way out): no runner starts (round 4, F5).</summary>
    ProbeInvalid,
}

/// <summary>
/// Tells the owner something in a short window, at most once a day for each topic, and never while the game runs (a window would pop up over it). The log and the status say it every time; this
/// is only the window. Moved out of the maintenance so the loop's own decisions (a public repository, a full disk) use the same rule.
/// </summary>
public sealed class OwnerNotices
{
    private static readonly TimeSpan s_interval = TimeSpan.FromHours(24);

    private readonly INotifier _notifier;
    private readonly StateStore _state;
    private readonly TimeProvider _time;

    public OwnerNotices(INotifier notifier, StateStore state, TimeProvider time)
    {
        _notifier = notifier;
        _state = state;
        _time = time;
    }

    /// <summary>Shows the notice unless one about this topic was shown in the last 24 hours or <paramref name="gateAllowed"/> is false (the game runs).</summary>
    /// <returns>True when the notice was shown.</returns>
    public bool OncePerDay(NoticeTopic topic, string text, bool gateAllowed)
    {
        var now = _time.GetUtcNow();
        if (!gateAllowed || !Due(Last(_state.Load(), topic), now))
        {
            return false;
        }

        _state.Update(s => Remember(s, topic, now));
        _notifier.Notify(text);
        return true;
    }

    /// <summary>Due when never shown, when a day has passed, or when the recorded time is in the future (the PC's clock was set back).</summary>
    private static bool Due(DateTimeOffset? last, DateTimeOffset now) => last is null || last.Value > now || now - last.Value >= s_interval;

    private static DateTimeOffset? Last(CiState state, NoticeTopic topic) => topic switch
    {
        NoticeTopic.Token => state.LastTokenNotice,
        NoticeTopic.Residue => state.LastResidueNotice,
        NoticeTopic.PublicRepository => state.LastPublicNotice,
        NoticeTopic.DiskFull => state.LastDiskNotice,
        NoticeTopic.ProbeInvalid => state.LastProbeNotice,
        _ => throw new ArgumentOutOfRangeException(nameof(topic), topic, "unknown notice topic"),
    };

    private static void Remember(CiState state, NoticeTopic topic, DateTimeOffset now)
    {
        switch (topic)
        {
            case NoticeTopic.Token:
                state.LastTokenNotice = now;
                break;
            case NoticeTopic.Residue:
                state.LastResidueNotice = now;
                break;
            case NoticeTopic.PublicRepository:
                state.LastPublicNotice = now;
                break;
            case NoticeTopic.DiskFull:
                state.LastDiskNotice = now;
                break;
            case NoticeTopic.ProbeInvalid:
                state.LastProbeNotice = now;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(topic), topic, "unknown notice topic");
        }
    }
}
