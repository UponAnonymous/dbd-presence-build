using System.Text.Json;

namespace DbdPresence.Discord.Ipc;

/// <summary>
/// The few fields of an incoming JSON frame the client needs. Everything else, notably the <c>user</c> object
/// of the <c>READY</c> event, is never read or retained (privacy, SAFETY_AND_PRIVACY). Text is sanitised on
/// the way in (FR-RPC-08).
/// </summary>
/// <param name="Command">The <c>cmd</c> value.</param>
/// <param name="Event">The <c>evt</c> value.</param>
/// <param name="Nonce">The <c>nonce</c> that correlates a response with its request.</param>
/// <param name="Code">The error code of <c>data.code</c> (or the root <c>code</c> of a CLOSE payload).</param>
/// <param name="Message">The error text of <c>data.message</c> (or the root <c>message</c> of a CLOSE payload), already passed through <see cref="DiscordText.Sanitize"/>; <see langword="null"/> if absent or empty after cleaning.</param>
internal readonly record struct IpcMessage(string? Command, string? Event, string? Nonce, int? Code, string? Message)
{
    /// <summary>Parses a <c>FRAME</c> payload.</summary>
    /// <exception cref="JsonException">The payload is not valid JSON.</exception>
    /// <exception cref="IpcProtocolException">The payload is not a JSON object.</exception>
    public static IpcMessage ParseFrame(ReadOnlyMemory<byte> payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = RequireObject(document);
        int? code = null;
        string? message = null;
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            code = ReadInt(data, "code");
            message = DiscordText.Sanitize(ReadString(data, "message"));
        }

        return new IpcMessage(ReadString(root, "cmd"), ReadString(root, "evt"), ReadString(root, "nonce"), code, message);
    }

    /// <summary>Parses a <c>CLOSE</c> payload such as <c>{"code":4000,"message":"Invalid Client ID"}</c>. An empty payload yields no code or message.</summary>
    /// <exception cref="JsonException">The payload is not valid JSON.</exception>
    /// <exception cref="IpcProtocolException">The payload is not a JSON object.</exception>
    public static IpcMessage ParseClose(ReadOnlyMemory<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return default;
        }

        using var document = JsonDocument.Parse(payload);
        var root = RequireObject(document);
        return new IpcMessage(null, null, null, ReadInt(root, "code"), DiscordText.Sanitize(ReadString(root, "message")));
    }

    private static JsonElement RequireObject(JsonDocument document) =>
        document.RootElement.ValueKind == JsonValueKind.Object
            ? document.RootElement
            : throw new IpcProtocolException("The IPC payload is not a JSON object.");

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;
}
