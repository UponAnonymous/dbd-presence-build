using DbdPresence.Vision.Imaging;

namespace DbdPresence.Vision.Evaluation;

/// <summary>
/// Decides, once per study session, whether its pictures include a window title bar and border and must be cut to the game's own picture before
/// any HUD region is applied. A recording that says it was cut to the client area is used as it is; a recording of a non-windowed game has no frame;
/// only a windowed recording with no flag (the owner's first three, 1922x1112) goes through the <see cref="ClientAreaNormaliser"/> heuristic. Labels
/// are never read or changed here.
/// </summary>
public static class ClientAreaSessions
{
    /// <summary>Decides for one session.</summary>
    /// <param name="session">The session.</param>
    /// <param name="decoder">Reads the pictures that are sampled.</param>
    public static ClientAreaDecision Decide(DatasetSession session, IFrameDecoder decoder)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(decoder);
        var m = session.Metadata;
        if (m.ClientAreaOnly == true)
        {
            return new ClientAreaDecision(ClientAreaOutcome.NotNeeded, null, m.Width, m.Height, "Recorded as the game's client area (the recorder cut each picture); used as it is.");
        }

        if (!string.Equals(m.WindowMode, "Windowed", StringComparison.Ordinal))
        {
            return new ClientAreaDecision(ClientAreaOutcome.NotNeeded, null, m.Width, m.Height, $"{m.WindowMode} recording: the window has no title bar; used as it is.");
        }

        if (session.Frames.Count == 0)
        {
            return new ClientAreaDecision(ClientAreaOutcome.Unverified, null, m.Width, m.Height, "The session lists no pictures to examine; nothing was normalised.");
        }

        var samples = ClientAreaNormaliser.SampleIndexes(session.Frames.Count)
            .Select(i => decoder.Decode(Path.Combine(session.Directory, session.Frames[i].File)))
            .ToList();
        var decision = ClientAreaNormaliser.Decide(samples);
        return m.ClientAreaOnly == false
            ? decision with { Reason = "The recorder says the pictures were not cut to the client area. " + decision.Reason }
            : decision;
    }
}

/// <summary>Hands out the pictures of one session cut by a <see cref="ClientAreaDecision"/>, so the detectors never see the title bar.</summary>
public sealed class ClientAreaDecoder : IFrameDecoder
{
    private readonly IFrameDecoder _inner;
    private readonly ClientAreaDecision _decision;

    /// <summary>Initializes a new instance of the <see cref="ClientAreaDecoder"/> class.</summary>
    public ClientAreaDecoder(IFrameDecoder inner, ClientAreaDecision decision)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(decision);
        _inner = inner;
        _decision = decision;
    }

    /// <inheritdoc />
    public ImageBuffer Decode(string path) => ClientAreaNormaliser.Apply(_inner.Decode(path), _decision);
}
