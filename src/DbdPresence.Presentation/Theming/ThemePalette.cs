namespace DbdPresence.Presentation.Theming;

/// <summary>
/// The design tokens (docs/UI_SPEC.md §4) — the single source of truth for colours. The WPF layer turns
/// them into brushes; tests verify every documented contrast pair.
/// </summary>
public sealed record ThemePalette
{
    /// <summary>Gets the window background.</summary>
    public required ColorRgb Bg { get; init; }

    /// <summary>Gets the panel surface.</summary>
    public required ColorRgb Surface { get; init; }

    /// <summary>Gets the raised surface (inputs, hover base).</summary>
    public required ColorRgb SurfaceRaised { get; init; }

    /// <summary>Gets the hover surface.</summary>
    public required ColorRgb SurfaceHover { get; init; }

    /// <summary>Gets the decorative separator colour (not used to identify controls).</summary>
    public required ColorRgb Border { get; init; }

    /// <summary>Gets the control boundary colour (≥ 3:1 against all surfaces).</summary>
    public required ColorRgb BorderStrong { get; init; }

    /// <summary>Gets the primary text colour.</summary>
    public required ColorRgb Text { get; init; }

    /// <summary>Gets the secondary text colour.</summary>
    public required ColorRgb TextMuted { get; init; }

    /// <summary>Gets the brand emphasis colour (default accent).</summary>
    public required ColorRgb AccentGold { get; init; }

    /// <summary>Gets the interactive/focus colour.</summary>
    public required ColorRgb AccentBlue { get; init; }

    /// <summary>Gets the success colour.</summary>
    public required ColorRgb Success { get; init; }

    /// <summary>Gets the warning colour.</summary>
    public required ColorRgb Warning { get; init; }

    /// <summary>Gets the danger colour.</summary>
    public required ColorRgb Danger { get; init; }

    /// <summary>Gets the tint of the status headline when all is well (design §9.9). Carries text and icons only, never an input control.</summary>
    public required ColorRgb SuccessSoft { get; init; }

    /// <summary>Gets the tint of the status headline when something needs attention, and of the "Testing" mark.</summary>
    public required ColorRgb WarningSoft { get; init; }

    /// <summary>Gets the tint of the status headline when something is wrong, and of inline errors.</summary>
    public required ColorRgb DangerSoft { get; init; }

    /// <summary>Gets the fill of the selected style card.</summary>
    public required ColorRgb AccentSoft { get; init; }

    /// <summary>Gets the preview card background (approximates Discord's card).</summary>
    public required ColorRgb PreviewCard { get; init; }

    /// <summary>Gets the preview card primary text.</summary>
    public required ColorRgb PreviewText { get; init; }

    /// <summary>Gets the preview card secondary text.</summary>
    public required ColorRgb PreviewMuted { get; init; }

    /// <summary>Gets a value indicating whether this is a dark palette.</summary>
    public required bool IsDark { get; init; }

    /// <summary>The dark palette.</summary>
    public static ThemePalette Dark { get; } = new()
    {
        IsDark = true,
        Bg = ColorRgb.Parse("#0E1116"),
        Surface = ColorRgb.Parse("#151A21"),
        SurfaceRaised = ColorRgb.Parse("#1C232C"),
        SurfaceHover = ColorRgb.Parse("#232B36"),
        Border = ColorRgb.Parse("#2A333E"),
        BorderStrong = ColorRgb.Parse("#6B7785"),
        Text = ColorRgb.Parse("#E8EDF2"),
        TextMuted = ColorRgb.Parse("#9AA6B2"),
        AccentGold = ColorRgb.Parse("#D4A649"),
        AccentBlue = ColorRgb.Parse("#5B9BFF"),
        Success = ColorRgb.Parse("#3FB950"),
        Warning = ColorRgb.Parse("#E3A008"),
        Danger = ColorRgb.Parse("#F06A6A"),
        SuccessSoft = ColorRgb.Parse("#1B3028"),
        WarningSoft = ColorRgb.Parse("#322D1E"),
        DangerSoft = ColorRgb.Parse("#34252B"),
        AccentSoft = ColorRgb.Parse("#302E27"),
        PreviewCard = ColorRgb.Parse("#232428"),
        PreviewText = ColorRgb.Parse("#F2F3F5"),
        PreviewMuted = ColorRgb.Parse("#B5BAC1"),
    };

    /// <summary>The light palette.</summary>
    public static ThemePalette Light { get; } = new()
    {
        IsDark = false,
        Bg = ColorRgb.Parse("#F6F7F9"),
        Surface = ColorRgb.Parse("#FFFFFF"),
        SurfaceRaised = ColorRgb.Parse("#F0F2F5"),
        SurfaceHover = ColorRgb.Parse("#E6E9EE"),
        Border = ColorRgb.Parse("#D5DAE1"),
        BorderStrong = ColorRgb.Parse("#7D8794"),
        Text = ColorRgb.Parse("#14181D"),
        TextMuted = ColorRgb.Parse("#55606C"),
        AccentGold = ColorRgb.Parse("#8C6512"),
        AccentBlue = ColorRgb.Parse("#1F5FCC"),
        Success = ColorRgb.Parse("#1A7F37"),
        Warning = ColorRgb.Parse("#875A00"),
        Danger = ColorRgb.Parse("#C62828"),
        SuccessSoft = ColorRgb.Parse("#EDF5EF"),
        WarningSoft = ColorRgb.Parse("#F5F2EB"),
        DangerSoft = ColorRgb.Parse("#FAEEEE"),
        AccentSoft = ColorRgb.Parse("#F6F3EC"),
        PreviewCard = ColorRgb.Parse("#F2F3F5"),
        PreviewText = ColorRgb.Parse("#060607"),
        PreviewMuted = ColorRgb.Parse("#4E5058"),
    };

    /// <summary>The surfaces any text or control may sit on.</summary>
    public IEnumerable<ColorRgb> Surfaces => [Bg, Surface, SurfaceRaised];
}
