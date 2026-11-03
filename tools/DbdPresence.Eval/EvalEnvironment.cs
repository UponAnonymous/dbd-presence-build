using DbdPresence.Core.Capture;
using DbdPresence.Study;

namespace DbdPresence.Eval;

/// <summary>Which text recogniser a run uses.</summary>
internal enum OcrKind
{
    /// <summary>Windows.Media.Ocr restricted to English: the only recogniser whose results can be reported as measurements.</summary>
    Windows,

    /// <summary>A fake recogniser. It exists for the Linux tests only; a run with it is always marked mechanics-only.</summary>
    Fake,
}

/// <summary>Everything the commands need from the machine, so the whole tool runs on Linux in tests with fakes: the labelling environment plus the recogniser factory.</summary>
internal sealed class EvalEnvironment : StudyEnvironment
{
    /// <summary>Gets the recogniser factory; null from the factory means that recogniser is not available here.</summary>
    public required Func<OcrKind, ITextRecognizer?> CreateRecognizer { get; init; }
}
