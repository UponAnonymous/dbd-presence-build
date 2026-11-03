using DbdPresence.Core.Online;

namespace DbdPresence.Configuration;

/// <summary>What screen detection may do once the user's own settings and the signed remote configuration are both considered.</summary>
/// <param name="DetectionAllowed">Detection may run.</param>
/// <param name="CaptureAllowed">Screen capture may run (detection reads the screen, so it needs detection too).</param>
public sealed record EffectiveDetection(bool DetectionAllowed, bool CaptureAllowed);

/// <summary>
/// The one place the signed remote configuration is applied to detection and capture (FR-ONL-08, SAFETY_AND_PRIVACY §2.3). The result is the
/// user's own opt-in <b>and</b> not forced off: the remote side can only take capability away. No input of this type can switch anything on.
/// </summary>
public static class RemoteFeatureGate
{
    /// <summary>Combines the user's detection settings with the remote flags.</summary>
    /// <param name="settings">The user's detection settings (off by default; enabled only after the risk notice).</param>
    /// <param name="remote">The merged remote configuration.</param>
    /// <returns>What may run.</returns>
    public static EffectiveDetection For(DetectionSettings settings, RemoteConfigSnapshot remote)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(remote);
        var detection = settings.Enabled && settings.RiskAcknowledged && !remote.DetectionForcedOff;
        return new EffectiveDetection(detection, detection && !remote.CaptureForcedOff);
    }
}
