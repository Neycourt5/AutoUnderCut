using System.Collections.Concurrent;
using Dalamud.Plugin.Services;

namespace SmartUndercutBot.Services;

public enum AutomationLogLevel
{
    Debug,
    Information,
    Warning,
    Error,
}

public sealed record AutomationLogEntry(DateTimeOffset Timestamp, AutomationLogLevel Level, string Message);

public sealed class AutomationLog
{
    private const int Capacity = 500;
    private readonly ConcurrentQueue<AutomationLogEntry> entries = new();
    private readonly IPluginLog pluginLog;

    public AutomationLog(IPluginLog pluginLog) => this.pluginLog = pluginLog;

    public void Add(AutomationLogLevel level, string message)
    {
        entries.Enqueue(new AutomationLogEntry(DateTimeOffset.Now, level, message));
        while (entries.Count > Capacity)
            entries.TryDequeue(out _);

        switch (level)
        {
            case AutomationLogLevel.Debug: pluginLog.Debug(message); break;
            case AutomationLogLevel.Information: pluginLog.Information(message); break;
            case AutomationLogLevel.Warning: pluginLog.Warning(message); break;
            case AutomationLogLevel.Error: pluginLog.Error(message); break;
        }
    }

    public IReadOnlyList<AutomationLogEntry> Snapshot() => entries.ToArray();
}
