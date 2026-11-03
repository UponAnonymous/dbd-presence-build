using System.Text;
using System.Text.Json;
using DbdPresence.Discord.Ipc;

namespace DbdPresence.Discord.Tests.Support;

/// <summary>A frame the fake Discord server received from the client.</summary>
/// <param name="Opcode">The opcode.</param>
/// <param name="Payload">The raw payload bytes.</param>
/// <param name="At">The (fake) time of receipt.</param>
internal sealed record ReceivedFrame(IpcOpcode Opcode, byte[] Payload, DateTimeOffset At)
{
    /// <summary>Gets the payload as text.</summary>
    public string Text => Encoding.UTF8.GetString(Payload);

    /// <summary>Gets the <c>cmd</c> property of a JSON payload, or null.</summary>
    public string? Command => Root.TryGetProperty("cmd", out var cmd) ? cmd.GetString() : null;

    /// <summary>Gets the <c>nonce</c> property of a JSON payload, or null.</summary>
    public string? Nonce => Root.TryGetProperty("nonce", out var nonce) ? nonce.GetString() : null;

    /// <summary>Gets <c>args.activity</c> of a SET_ACTIVITY payload (<c>Null</c> kind when clearing).</summary>
    public JsonElement Activity => Root.GetProperty("args").GetProperty("activity");

    /// <summary>Gets <c>args.activity.details</c> or null (also null when the activity is cleared).</summary>
    public string? Details =>
        Activity.ValueKind == JsonValueKind.Object && Activity.TryGetProperty("details", out var details) ? details.GetString() : null;

    /// <summary>Gets a value indicating whether this is a SET_ACTIVITY command.</summary>
    public bool IsSetActivity => Opcode == IpcOpcode.Frame && Command == "SET_ACTIVITY";

    /// <summary>Gets a value indicating whether this is a SET_ACTIVITY that clears the presence.</summary>
    public bool IsClear => IsSetActivity && Activity.ValueKind == JsonValueKind.Null;

    private JsonElement Root
    {
        get
        {
            using var document = JsonDocument.Parse(Payload);
            return document.RootElement.Clone();
        }
    }
}
