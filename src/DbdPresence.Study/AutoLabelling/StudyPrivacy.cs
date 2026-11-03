namespace DbdPresence.Study;

/// <summary>
/// The words about where the review pictures go when the owner's app labels a recording (ADR-0018, SAFETY_AND_PRIVACY section 3.3). The Study page shows them
/// before the first upload and the run writes them to the log. They name no provider or model: the owner's service holds that (project rule 7).
/// </summary>
public static class StudyPrivacy
{
    /// <summary>The heading of the one-time confirmation.</summary>
    public const string ConfirmTitle = "Turn on automatic labelling";

    /// <summary>The one-line version, printed to the log whenever a review file is made and shown under the page title once confirmed.</summary>
    public const string Notice =
        "The review pictures show other players' names and anything else that was on your screen. Labelling sends them through your own service to your labelling account; nothing is stored on the service. The pictures stay on this PC and are deleted with the recording after 30 days.";

    /// <summary>The paragraphs of the one-time confirmation, in reading order.</summary>
    public static IReadOnlyList<string> ConfirmParagraphs { get; } =
    [
        "To label a recording, this app sends the pictures of its review file (screens, close-ups of text, times) through your own service to the labelling account you set up in step 8a of the Cloudflare guide (docs/owner/cloudflare-setup.md).",
        "The pictures can show other players' names and anything else that was on your screen. They are personal data of those players. The labelling account's provider keeps what it receives only as its own terms say; read them before you confirm.",
        "Nothing is stored on your service: it passes the pictures on and keeps only sizes and token counts. On this PC the pictures stay in the recording's folder until the 30-day clean-up deletes them.",
        "Until you confirm, nothing is sent, neither after you play nor with “Label new recordings now”. You can turn labelling off again at any time.",
    ];

    /// <summary>The text of the button that confirms.</summary>
    public const string ConfirmButton = "Turn on automatic labelling";
}
