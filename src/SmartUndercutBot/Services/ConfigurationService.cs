using Dalamud.Plugin;

namespace SmartUndercutBot.Services;

public sealed class ConfigurationService
{
    private readonly IDalamudPluginInterface pluginInterface;

    public ConfigurationService(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
        Current = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        var oldVersion = Current.Version;
        Current.Normalize();
        if (Current.Version != oldVersion)
            pluginInterface.SavePluginConfig(Current);
    }

    public Configuration Current { get; }

    public void Save()
    {
        Current.Normalize();
        pluginInterface.SavePluginConfig(Current);
    }
}
