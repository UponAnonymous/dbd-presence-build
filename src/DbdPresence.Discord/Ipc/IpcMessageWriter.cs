using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using DbdPresence.Core.Presence;

namespace DbdPresence.Discord.Ipc;

/// <summary>
/// Builds the JSON payloads the client sends. Activities are mapped field by field from an already validated
/// <see cref="ValidatedActivity"/>; nothing is re-validated here (FR-RPC-04; buttons FR-RPC-09, activity type FR-RPC-10, status display FR-RPC-11). Null or empty fields
/// are omitted, and the timer start is written in Unix <em>milliseconds</em>.
/// </summary>
internal static class IpcMessageWriter
{
    // The payload goes to a local pipe, never into HTML, so the relaxed encoder keeps text readable and compact.
    private static readonly JsonWriterOptions s_options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The handshake payload: <c>{"v":1,"client_id":"…"}</c>.</summary>
    public static byte[] Handshake(string clientId)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientId);
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteString("client_id", clientId);
            writer.WriteEndObject();
        });
    }

    /// <summary>A <c>SET_ACTIVITY</c> command. A <see langword="null"/> activity clears the presence.</summary>
    public static byte[] SetActivity(int pid, ValidatedActivity? activity, string nonce)
    {
        ArgumentException.ThrowIfNullOrEmpty(nonce);
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("cmd", "SET_ACTIVITY");
            writer.WriteStartObject("args");
            writer.WriteNumber("pid", pid);
            if (activity is null)
            {
                writer.WriteNull("activity");
            }
            else
            {
                writer.WritePropertyName("activity");
                WriteActivity(writer, activity);
            }

            writer.WriteEndObject();
            writer.WriteString("nonce", nonce);
            writer.WriteEndObject();
        });
    }

    private static void WriteActivity(Utf8JsonWriter writer, ValidatedActivity activity)
    {
        writer.WriteStartObject();
        writer.WriteNumber("type", (int)activity.Kind);
        WriteIfPresent(writer, "details", activity.Details);
        WriteIfPresent(writer, "state", activity.State);

        if (activity.StartTimestamp is { } start)
        {
            writer.WriteStartObject("timestamps");
            writer.WriteNumber("start", start.ToUnixTimeMilliseconds());
            writer.WriteEndObject();
        }

        if (activity.LargeImage is not null || activity.LargeText is not null
            || activity.SmallImage is not null || activity.SmallText is not null)
        {
            writer.WriteStartObject("assets");
            WriteIfPresent(writer, "large_image", activity.LargeImage);
            WriteIfPresent(writer, "large_text", activity.LargeText);
            WriteIfPresent(writer, "small_image", activity.SmallImage);
            WriteIfPresent(writer, "small_text", activity.SmallText);
            writer.WriteEndObject();
        }

        if (activity.Buttons.Count > 0)
        {
            writer.WriteStartArray("buttons");
            foreach (var button in activity.Buttons)
            {
                writer.WriteStartObject();
                writer.WriteString("label", button.Label);
                writer.WriteString("url", button.Url);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        if (activity.StatusDisplay is { } display)
        {
            writer.WriteNumber("status_display_type", (int)display);
        }

        WriteIfPresent(writer, "name", activity.NameOverride);
        writer.WriteEndObject();
    }

    private static void WriteIfPresent(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            writer.WriteString(name, value);
        }
    }

    private static byte[] Write(Action<Utf8JsonWriter> build)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, s_options))
        {
            build(writer);
        }

        return buffer.WrittenSpan.ToArray();
    }
}
