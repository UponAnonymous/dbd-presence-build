using DbdPresence.Packs.Roster;
using DbdPresence.Presentation.Characters;
using DbdPresence.Vision.Text;

namespace DbdPresence.Presentation.Detection;

/// <summary>
/// Hands a verified signed roster to the live detection loop as well as to the Characters page (D35, FR-ROS-04): after the page accepted the roster, the
/// loop and the status line's names start using the embedded vocabulary plus the roster's new characters, with no restart. It wraps the page's consumer and
/// changes nothing when that one refuses (it throws <see cref="FormatException"/> first). The loop swaps its lists between frames
/// (<see cref="DetectionLoop.UpdateVocabulary"/>).
/// </summary>
public sealed class DetectionRosterConsumer : IRosterConsumer
{
    private readonly IRosterConsumer _inner;
    private readonly Vocabulary _embedded;
    private readonly DetectionLoop _loop;
    private readonly DetectionNames _names;

    /// <summary>Initializes a new instance of the <see cref="DetectionRosterConsumer"/> class.</summary>
    /// <param name="inner">The consumer that runs first (the Characters page).</param>
    /// <param name="embeddedVocabulary">The embedded vocabulary, the append-only baseline.</param>
    /// <param name="loop">The live loop.</param>
    /// <param name="names">The words for the status line.</param>
    public DetectionRosterConsumer(IRosterConsumer inner, Vocabulary embeddedVocabulary, DetectionLoop loop, DetectionNames names)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(embeddedVocabulary);
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(names);
        _inner = inner;
        _embedded = embeddedVocabulary;
        _loop = loop;
        _names = names;
    }

    /// <inheritdoc />
    public void Apply(RosterDocument roster)
    {
        ArgumentNullException.ThrowIfNull(roster);
        _inner.Apply(roster);
        var merged = RosterVocabulary.Merge(_embedded, roster);
        _loop.UpdateVocabulary(merged);
        _names.Update(merged);
    }
}
