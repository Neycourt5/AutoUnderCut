using Dalamud.Plugin;

namespace SmartUndercutBot.Services;

public sealed class ConfigurationService
{
    private readonly IDalamudPluginInterface pluginInterface;

    public ConfigurationService(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
        Configuration? loaded = null;
        try
        {
            loaded = pluginInterface.GetPluginConfig() as Configuration;
        }
        catch (Exception)
        {
            // Handled below: an unreadable file is preserved, never silently replaced.
        }
        PreserveSettingsFile(loaded is not null);
        Current = loaded ?? new Configuration();
        var oldVersion = Current.Version;
        Current.Normalize();
        if (Current.Version != oldVersion)
            pluginInterface.SavePluginConfig(Current);
    }

    public Configuration Current { get; }

    /// <summary>Set when an existing settings file could not be loaded; the copy kept is named here.</summary>
    public string? UnreadableSettingsCopy { get; private set; }

    public void Save()
    {
        Current.Normalize();
        pluginInterface.SavePluginConfig(Current);
    }

    /// <summary>
    /// The settings file holds every buying rule and every tracked cost floor. A file
    /// that exists but did not load is not a new install: keep a dated copy before
    /// defaults are saved over it. A file that did load is copied to <c>.bak</c> once
    /// per start, so a bad edit or a damaged write can always be rolled back.
    /// </summary>
    private void PreserveSettingsFile(bool loaded)
    {
        try
        {
            var file = pluginInterface.ConfigFile;
            if (!file.Exists || file.Length == 0)
                return;
            if (loaded)
            {
                File.Copy(file.FullName, file.FullName + ".bak", overwrite: true);
                return;
            }
            var copy = $"{file.FullName}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(file.FullName, copy, overwrite: false);
            UnreadableSettingsCopy = copy;
        }
        catch (Exception)
        {
            // Best effort: a copy that cannot be made must not stop the plugin loading.
        }
    }
}
