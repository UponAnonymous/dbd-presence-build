using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace DbdPresence.Presentation.Presence;

/// <summary>A button that fixes the cause a <see cref="PreviewHint"/> names.</summary>
/// <param name="Label">The button text.</param>
/// <param name="Command">What it does.</param>
public sealed record PreviewHintAction(string Label, ICommand Command);

/// <summary>One plain sentence under a preview saying why something Discord could show is missing (D29).</summary>
/// <param name="Id">A stable id for tests and automation.</param>
/// <param name="Message">The sentence.</param>
/// <param name="Actions">Fix buttons; empty when only the user's game or packs can change it.</param>
public sealed record PreviewHint(string Id, string Message, IReadOnlyList<PreviewHintAction> Actions);

/// <summary>What the hints need to know; all of it is the real, live situation.</summary>
/// <param name="Paused">True while presence is paused.</param>
/// <param name="GenericWhilePaused">True when "While paused: show that I'm playing" is on.</param>
/// <param name="GameNotRunning">True when the situation is the game not running.</param>
/// <param name="ActivityShown">True when the situation composes an activity (false = nothing shown on Discord).</param>
/// <param name="Preview">The preview card as built from the composed activity.</param>
public sealed record PreviewHintInputs(bool Paused, bool GenericWhilePaused, bool GameNotRunning, bool ActivityShown, ActivityPreview Preview);

/// <summary>Builds the few most common "why is this missing" hints for a preview (D29).</summary>
public static class PreviewHints
{
    /// <summary>Builds the hints for <paramref name="inputs"/>.</summary>
    /// <param name="inputs">The live situation.</param>
    /// <param name="resume">Resumes presence.</param>
    public static IReadOnlyList<PreviewHint> Build(PreviewHintInputs inputs, Action resume)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(resume);
        var hints = new List<PreviewHint>();
        if (inputs.Paused)
        {
            hints.Add(new PreviewHint(
                "paused",
                inputs.ActivityShown
                    ? inputs.GenericWhilePaused
                        ? "Paused: Discord only shows that you're playing right now. This is what it shows when you resume."
                        : "Paused: Discord shows nothing right now. This is what it shows when you resume."
                    : "Paused: Discord shows nothing right now, and nothing will show after you resume at this moment.",
                [new PreviewHintAction("Resume", new RelayCommand(resume))]));
        }

        if (inputs.GameNotRunning && !inputs.ActivityShown)
        {
            hints.Add(new PreviewHint("game-closed", "The game is not running, so Discord shows nothing. It shows your status once the game starts.", []));
        }

        foreach (var image in new[] { inputs.Preview.LargeImage, inputs.Preview.SmallImage })
        {
            if (image is { Kind: ImageTileKind.AssetKey })
            {
                hints.Add(new PreviewHint(
                    $"asset-{image.Reference}",
                    "Discord shows its own picture here, so the app can't preview it.",
                    []));
            }
        }

        return hints;
    }
}
