using DbdPresence.Configuration;
using DbdPresence.Core.Detection;
using DbdPresence.Vision.Screens;

namespace DbdPresence.Presentation.Detection;

/// <summary>
/// How long the live loop waits between cycles (FR-DET-25). The HUD profile sets the cadence (its menu and trial intervals, a floor, the low-impact
/// factor) and the busy-time budget; the user's scan-interval settings can only ask for <em>slower</em> sampling than the profile, never faster.
/// A cycle that took long makes the loop wait longer, so the share of wall time spent inside cycles stays within the profile's budget. Pure and
/// clock-free.
/// </summary>
public static class DetectionCadence
{
    /// <summary>How often the loop looks at its settings while it is off or waiting for the game.</summary>
    public static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(1);

    /// <summary>Returns the delay before the next cycle.</summary>
    /// <param name="sampling">The profile's sampling values.</param>
    /// <param name="settings">The user's detection settings.</param>
    /// <param name="scene">The scene the engine has confirmed.</param>
    /// <param name="busy">How long the cycle that just ended took.</param>
    public static TimeSpan NextDelay(HudSampling sampling, DetectionSettings settings, Scene scene, TimeSpan busy)
    {
        ArgumentNullException.ThrowIfNull(sampling);
        ArgumentNullException.ThrowIfNull(settings);
        var inTrial = scene is Scene.InTrial or Scene.Spectating or Scene.InMatchMenu;
        var profile = inTrial ? sampling.TrialInterval : sampling.MenuInterval;
        var user = TimeSpan.FromMilliseconds(inTrial ? settings.TrialScanIntervalMs : settings.MenuScanIntervalMs);

        // The profile guarantees MinInterval <= both of its intervals (HudSampling.Validate), so the result never falls below the floor.
        var interval = profile > user ? profile : user;
        if (settings.LowImpact)
        {
            interval *= sampling.LowImpactFactor;
        }

        // busy / (busy + delay) <= budget  <=>  delay >= busy * (1 / budget - 1)
        var budgetDelay = busy <= TimeSpan.Zero ? TimeSpan.Zero : busy * ((1.0 / sampling.BusyBudget) - 1.0);
        return budgetDelay > interval ? budgetDelay : interval;
    }
}
