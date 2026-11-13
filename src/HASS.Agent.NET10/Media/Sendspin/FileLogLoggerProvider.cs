using HASS.Agent.Companion.Logging;
using Microsoft.Extensions.Logging;

namespace HASS.Agent.Companion.Media.Sendspin;

/// <summary>
/// The Sendspin SDK's logging into the app's own log. The SDK tells a lot at Information (every
/// group update, every handshake step), which would bury the rest of the log: that goes in only
/// with verbose logging on. Its warnings and errors go in, but for those about the stream and
/// the clock, and for connections that failed or closed, which <see cref="ConnectionFailures"/> holds back.
/// </summary>
internal sealed class FileLogLoggerProvider(FileLog log, ConnectionFailures? failures = null) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new FileLogLogger(log, ShortName(categoryName), failures);

    public void Dispose()
    {
    }

    // "Sendspin.SDK.Audio.TimedAudioBuffer" -> "TimedAudioBuffer": the log line says Sendspin already.
    private static string ShortName(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot >= 0 ? category[(dot + 1)..] : category;
    }

    /// <summary>
    /// A clock that lost its convergence and measures again: after the PC wakes it always
    /// does, and the player stays in sync while it converges anew.
    /// </summary>
    internal static bool IsClockNote(string message) => message.StartsWith("[ClockSync]", StringComparison.Ordinal);

    private sealed class FileLogLogger(FileLog log, string category, ConnectionFailures? failures) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        // The buffer and the sync correction warn about the stream itself: a gap in what the
        // server sends (a radio station has one every few seconds), an underrun it covers. They
        // are what verbose logging is for, and would fill the log in an evening of radio.
        private readonly bool _streamNotes = category is "TimedAudioBuffer" or "SyncCorrectedSampleSource";

        public bool IsEnabled(LogLevel logLevel) => logLevel switch
        {
            LogLevel.None => false,
            LogLevel.Trace or LogLevel.Debug or LogLevel.Information => log.VerboseEnabled,
            LogLevel.Warning when _streamNotes => log.VerboseEnabled,
            _ => true
        };

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var text = formatter(state, exception);
            var message = $"Sendspin {category}: {text}";
            switch (logLevel)
            {
                case LogLevel.Warning when _streamNotes || IsClockNote(text):
                    log.Debug(message);
                    break;
                case LogLevel.Warning when failures is not null && ConnectionFailures.IsFailure(category, text):
                    failures.Failed(exception is null ? message : $"{message} ({exception.Message})");
                    break;
                case LogLevel.Warning:
                    log.Warning(exception is null ? message : $"{message} ({exception.Message})");
                    break;
                case LogLevel.Error or LogLevel.Critical:
                    if (exception is null)
                    {
                        log.Error(message);
                    }
                    else
                    {
                        log.Error(exception, message);
                    }

                    break;
                default:
                    log.Debug(message);
                    break;
            }
        }
    }
}
