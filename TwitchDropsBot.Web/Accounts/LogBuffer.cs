using System.Text;
using Serilog.Events;
using Serilog.Parsing;
using TwitchDropsBot.Web.Api;

namespace TwitchDropsBot.Web.Accounts;

/// <summary>The most recent log lines of one account, for the Activity view.</summary>
public sealed class LogBuffer
{
    private readonly object _lock = new();
    private readonly Queue<LogLineDto> _lines = new();
    private readonly int _capacity;
    private long _seq;

    public LogBuffer(int capacity = 500)
    {
        _capacity = capacity;
    }

    public void Add(LogEventLevel level, string message)
    {
        lock (_lock)
        {
            _lines.Enqueue(new LogLineDto(++_seq, DateTime.UtcNow, LevelName(level), Redactor.Clean(message)));
            while (_lines.Count > _capacity)
            {
                _lines.Dequeue();
            }
        }
    }

    public void Add(LogEvent logEvent) => Add(logEvent.Level, Render(logEvent));

    // Like LogEvent.RenderMessage, but strings are written as they are instead of in quotes
    private static string Render(LogEvent logEvent)
    {
        var builder = new StringBuilder();
        using var writer = new StringWriter(builder);
        foreach (var token in logEvent.MessageTemplate.Tokens)
        {
            if (token is PropertyToken property &&
                logEvent.Properties.TryGetValue(property.PropertyName, out var value) &&
                value is ScalarValue { Value: string text })
            {
                writer.Write(text);
            }
            else
            {
                token.Render(logEvent.Properties, writer);
            }
        }

        return builder.ToString();
    }

    public LogPageDto After(long seq, int max = 300)
    {
        lock (_lock)
        {
            var lines = _lines.Where(x => x.Seq > seq).TakeLast(max).ToList();
            return new LogPageDto(lines, _seq);
        }
    }

    private static string LevelName(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose or LogEventLevel.Debug => "debug",
        LogEventLevel.Information => "info",
        LogEventLevel.Warning => "warning",
        _ => "error"
    };
}
