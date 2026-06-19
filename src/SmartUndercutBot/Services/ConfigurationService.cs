using Dalamud.Plugin;

namespace SmartUndercutBot.Services;

public sealed class ConfigurationService
{
    private readonly IDalamudPluginInterface pluginInterface;

    public ConfigurationService(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
        Current = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Current.Normalize();
    }

    public Configuration Current { get; }

    public void Save()
    {
        Current.Normalize();
        pluginInterface.SavePluginConfig(Current);
    }
}
