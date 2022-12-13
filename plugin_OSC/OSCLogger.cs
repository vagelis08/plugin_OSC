using Amethyst.Plugins.Contract;
using Microsoft.Extensions.Logging;
using System;
using VRC.OSCQuery;

namespace plugin_OSC {
    public class OSCLogger : ILogger<OSCQueryService> {
        private IAmethystHost m_host;

        public OSCLogger(IAmethystHost host) {
            this.m_host = host;
        }

        public IDisposable BeginScope<TState>(TState state) {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) {

            switch ( logLevel ) {
                case LogLevel.Information:
                    m_host?.Log($"[VRC-OSCQuery] {state}", LogSeverity.Info);
                    break;
                case LogLevel.Warning:
                    m_host?.Log($"[VRC-OSCQuery] {state}", LogSeverity.Warning);
                    break;
                case LogLevel.Error:
                    m_host?.Log($"[VRC-OSCQuery] {state}", LogSeverity.Error);
                    break;
                case LogLevel.Critical:
                    m_host?.Log($"[VRC-OSCQuery] {state}", LogSeverity.Fatal);
                    break;
            }
        }
    }
}
