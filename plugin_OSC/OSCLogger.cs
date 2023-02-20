using Amethyst.Plugins.Contract;
using Microsoft.Extensions.Logging;
using VRC.OSCQuery;

namespace plugin_OSC;

public class OscLogger : ILogger<OSCQueryService>
{
    private readonly IAmethystHost _host;

    public OscLogger(IAmethystHost host)
    {
        _host = host;
    }

    public IDisposable BeginScope<TState>(TState state)
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
        Func<TState, Exception, string> formatter)
    {
        _host?.Log($"[VRC-OSCQuery] {state}", logLevel switch
        {
            LogLevel.Warning => LogSeverity.Warning,
            LogLevel.Error => LogSeverity.Error,
            LogLevel.Critical => LogSeverity.Fatal,
            _ => LogSeverity.Info
        });
    }
}