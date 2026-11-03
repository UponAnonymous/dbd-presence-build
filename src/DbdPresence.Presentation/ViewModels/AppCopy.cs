namespace DbdPresence.Presentation.ViewModels;

/// <summary>User-facing statements shown in more than one place, so first run, About and the docs cannot drift apart (SAFETY_AND_PRIVACY §2.1).</summary>
public static class AppCopy
{
    /// <summary>What the app does with the game: current behaviour only, no roadmap.</summary>
    public const string ProcessCheckOnly = "By default it only checks whether the game is running. Screen reading is off until you turn it on in Settings; then it looks only at the Dead by Daylight window, never saves pictures of it, and never touches the game.";

    /// <summary>The short unofficial note under the name picker (Discord status).</summary>
    public const string NameIsOnlyText = "Unofficial fan-made app. This name is only text on your profile.";

    /// <summary>What the app connects to (ADR-0016): stated identically in first run and About.</summary>
    public const string NetworkStatement = "It talks to the Discord app on this PC and, because you sign in, to the DBD Presence service over HTTPS. It sends no telemetry and never loads pictures from the internet.";

    /// <summary>The title of the unlock screen (D32).</summary>
    public const string UnlockTitle = "Sign in to start";

    /// <summary>Why the app is locked while the access mode is invite-only (D32): honest and short.</summary>
    public const string LockedInviteOnly = "DBD Presence is invite-only for now. Sign in with Discord and enter your invite code.";

    /// <summary>Why the app asks for a sign-in while the access mode is public (D32): no invite is needed.</summary>
    public const string LockedPublic = "Sign in with Discord to start using DBD Presence. No invite code is needed right now.";

    /// <summary>What the sign-in shares, shown on the unlock screen next to the button (PLAN_V2 §4.3).</summary>
    public const string SignInShares = "Sign-in opens Discord in your browser. Only your Discord ID and display name are shared with the service; your profile picture is never shown here.";

    /// <summary>Gets the honest summary of what the service stores and why (PLAN_V2 §4.3, D32). The full notice is installed with the app.</summary>
    public static IReadOnlyList<string> AccountNotice { get; } =
    [
        "You must sign in with Discord to use DBD Presence. Until you do, nothing is sent to Discord.",
        "The service keeps your Discord user ID and display name, when you joined and last used it, your account status, a public key for each PC you sign in on, and session records. It does not keep your email address or an IP address history.",
        "Each daily check-in lets the service see your IP address for that request and records a last-seen time.",
        "Security and audit logs are kept for 90 days. Inactive accounts are deleted after 12 months. Rate-limit counters are kept for 24 hours.",
        "You can delete your account from the Account page. Deleted data can remain in backups for up to 30 days.",
        "Once you are in, your Discord status keeps working on this PC even when the service is unreachable. The app sends no telemetry and never loads images from the internet.",
    ];

    /// <summary>Behaviour Interactive's notice, shown on every pack card whose signed index flags Behaviour artwork (ADR-0013 condition 2).</summary>
    public const string BehaviourNotice = "© Behaviour Interactive. Unofficial, not affiliated with or endorsed by Behaviour Interactive.";
}
