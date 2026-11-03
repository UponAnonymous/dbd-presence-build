using DbdPresence.Core.Diagnostics;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;

namespace DbdPresence.App.Logging;

/// <summary>
/// A Serilog sink wrapper that removes the user's identity from every event before it reaches the real sink (NFR-PRIV-02, threat-model review TM-12):
/// each text property value goes through <see cref="LogRedaction.Redact"/>, and the exception is replaced by its redacted
/// <see cref="LogRedaction.Describe"/> text. Call sites that already redact are unaffected (redaction is idempotent); a new call site that logs a raw
/// message or exception is covered all the same. This file uses no WPF, so a Linux test project compiles it.
/// </summary>
internal sealed class RedactingSink(ILogEventSink inner, LogRedaction redaction) : ILogEventSink
{
    /// <inheritdoc />
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        var properties = logEvent.Properties.Select(p => new LogEventProperty(p.Key, Redact(p.Value)));
        Exception? exception = logEvent.Exception is null ? null : new RedactedException(redaction.Describe(logEvent.Exception));
        inner.Emit(new LogEvent(logEvent.Timestamp, logEvent.Level, exception, logEvent.MessageTemplate, properties));
    }

    private LogEventPropertyValue Redact(LogEventPropertyValue value) => value switch
    {
        ScalarValue { Value: null } scalar => scalar,
        ScalarValue { Value: string text } => new ScalarValue(redaction.Redact(text)),
        ScalarValue { Value: bool or char or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or Enum or Guid or DateTime or DateTimeOffset or TimeSpan } scalar => scalar,
        ScalarValue scalar => new ScalarValue(redaction.Redact(scalar.Value?.ToString())),
        SequenceValue sequence => new SequenceValue(sequence.Elements.Select(Redact)),
        StructureValue structure => new StructureValue(structure.Properties.Select(p => new LogEventProperty(p.Name, Redact(p.Value))), structure.TypeTag),
        DictionaryValue dictionary => new DictionaryValue(dictionary.Elements.Select(e => new KeyValuePair<ScalarValue, LogEventPropertyValue>((ScalarValue)Redact(e.Key), Redact(e.Value)))),
        _ => new ScalarValue(redaction.Redact(value.ToString())),
    };

    /// <summary>An exception stand-in whose text is the already redacted description, so a file sink that prints the exception prints nothing private.</summary>
    private sealed class RedactedException : Exception
    {
        private readonly string _description;

        public RedactedException(string description)
            : base(description)
        {
            _description = description;
        }

        public override string ToString() => _description;
    }
}

/// <summary>Adds the redacting wrapper to a Serilog configuration.</summary>
internal static class RedactingSinkExtensions
{
    /// <summary>Configures sinks whose events are redacted first.</summary>
    /// <param name="sinks">The sink configuration.</param>
    /// <param name="redaction">The redaction to apply.</param>
    /// <param name="configure">Configures the wrapped (real) sinks.</param>
    /// <returns>The logger configuration.</returns>
    public static Serilog.LoggerConfiguration Redacted(this LoggerSinkConfiguration sinks, LogRedaction redaction, Action<LoggerSinkConfiguration> configure)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        ArgumentNullException.ThrowIfNull(redaction);
        ArgumentNullException.ThrowIfNull(configure);
        return sinks.Sink(LoggerSinkConfiguration.Wrap(inner => new RedactingSink(inner, redaction), configure));
    }
}
