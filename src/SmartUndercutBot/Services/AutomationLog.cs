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
    private const int KeepSessionFiles = 10;
    private readonly ConcurrentQueue<AutomationLogEntry> entries = new();
    private readonly IPluginLog pluginLog;
    private readonly object fileSync = new();
    private readonly string? sessionFile;

    public AutomationLog(IPluginLog pluginLog, string? logDirectory = null)
    {
        this.pluginLog = pluginLog;
        sessionFile = TryStartSessionFile(logDirectory);
    }

    /// <summary>Full path of this session's log file, or null when file logging is unavailable.</summary>
    public string? SessionFilePath => sessionFile;

    // Everything, including Debug, goes to the file. The in-game view keeps only the
    // most recent entries, which is not enough to diagnose a run after the fact.
    private string? TryStartSessionFile(string? logDirectory)
    {
        if (string.IsNullOrWhiteSpace(logDirectory))
            return null;
        try
        {
            Directory.CreateDirectory(logDirectory);
            foreach (var stale in new DirectoryInfo(logDirectory)
                         .GetFiles("session-*.log")
                         .OrderByDescending(x => x.CreationTimeUtc)
                         .Skip(KeepSessionFiles - 1))
                stale.Delete();
            var path = Path.Combine(logDirectory, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.AppendAllText(path,
                $"Smart Undercutter session log started {DateTimeOffset.Now:u}{Environment.NewLine}");
            return path;
        }
        catch (Exception ex)
        {
            pluginLog.Warning($"Could not start the session log file: {ex.Message}");
            return null;
        }
    }

    public void Add(AutomationLogLevel level, string message)
    {
        entries.Enqueue(new AutomationLogEntry(DateTimeOffset.Now, level, message));
        while (entries.Count > Capacity)
            entries.TryDequeue(out _);
        WriteToFile(level, message);

        switch (level)
        {
            case AutomationLogLevel.Debug: pluginLog.Debug(message); break;
            case AutomationLogLevel.Information: pluginLog.Information(message); break;
            case AutomationLogLevel.Warning: pluginLog.Warning(message); break;
            case AutomationLogLevel.Error: pluginLog.Error(message); break;
        }
    }

    private void WriteToFile(AutomationLogLevel level, string message)
    {
        if (sessionFile is null)
            return;
        try
        {
            lock (fileSync)
                File.AppendAllText(sessionFile,
                    $"{DateTimeOffset.Now:HH:mm:ss.fff} [{level.ToString().ToUpperInvariant()[0]}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Never let logging break a run.
        }
    }

    public IReadOnlyList<AutomationLogEntry> Snapshot() => entries.ToArray();
}
