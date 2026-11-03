// Covers: NFR-PRIV-02 (logs carry no user identity) and threat-model review TM-12 (redaction at the sink, not only at each call site).
using DbdPresence.App.Logging;
using DbdPresence.Core.Diagnostics;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace DbdPresence.AppLogging.Tests;

public sealed class RedactingSinkTests
{
    private static readonly LogRedaction s_redaction = new(@"C:\Users\Alice", "Alice", "ALICE-PC");

    private static readonly string[] s_list = [@"C:\Users\Alice\b"];

    private sealed class Capture : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static (Serilog.Core.Logger Logger, Capture Sink) Create()
    {
        var sink = new Capture();
        var logger = new LoggerConfiguration().WriteTo.Redacted(s_redaction, inner => inner.Sink(sink)).CreateLogger();
        return (logger, sink);
    }

    [Fact]
    public void RenderedMessage_HasTheProfilePath_TheUserName_AndTheMachineNameMasked()
    {
        var (logger, sink) = Create();

        logger.Warning("Could not read {Path} on {Machine} for {User}", @"C:\Users\Alice\AppData\x.json", "ALICE-PC", "Alice");

        var text = Assert.Single(sink.Events).RenderMessage(System.Globalization.CultureInfo.InvariantCulture);
        Assert.DoesNotContain("Alice", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%", text, StringComparison.Ordinal);
        Assert.Contains("<machine>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Exception_IsReplacedByItsRedactedDescription()
    {
        var (logger, sink) = Create();
        var error = new IOException(@"No access to C:\Users\Alice\secret.txt");

        logger.Error(error, "failed");

        var logged = Assert.Single(sink.Events).Exception;
        Assert.NotNull(logged);
        Assert.DoesNotContain("Alice", logged.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Alice", logged.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IOException", logged.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void NestedValues_AreRedacted_AndPlainValuesPassThrough()
    {
        var (logger, sink) = Create();

        logger.Information("{@Detail} {List} {Count}", new { Path = @"C:\Users\Alice\a" }, s_list, 7);

        var logEvent = Assert.Single(sink.Events);
        var text = logEvent.RenderMessage(System.Globalization.CultureInfo.InvariantCulture);
        Assert.DoesNotContain("Alice", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(7, ((ScalarValue)logEvent.Properties["Count"]).Value);
    }

    [Fact]
    public void Redaction_IsIdempotent_ForCallSitesThatAlreadyRedact()
    {
        var (logger, sink) = Create();
        var once = s_redaction.Redact(@"C:\Users\Alice\a");

        logger.Information("{P}", once);

        Assert.Equal(once, ((ScalarValue)Assert.Single(sink.Events).Properties["P"]).Value);
    }

    [Fact]
    public void EventWithoutException_KeepsLevelAndTemplate()
    {
        var (logger, sink) = Create();

        logger.Warning("plain {X}", 1);

        var logEvent = Assert.Single(sink.Events);
        Assert.Equal(LogEventLevel.Warning, logEvent.Level);
        Assert.Null(logEvent.Exception);
        Assert.Equal("plain {X}", logEvent.MessageTemplate.Text);
    }
}
