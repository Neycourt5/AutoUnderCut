using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace SmartUndercutBot.Services;

public interface ILifestreamService
{
    bool IsAvailable { get; }
    bool IsBusy { get; }
    bool ChangeWorld(string worldName);
    bool ExecuteCommand(string arguments);
}

public sealed class LifestreamService : ILifestreamService
{
    private readonly ICallGateSubscriber<bool> isBusy;
    private readonly ICallGateSubscriber<string, bool> changeWorld;
    private readonly ICallGateSubscriber<string, object> executeCommand;

    public LifestreamService(IDalamudPluginInterface pluginInterface)
    {
        isBusy = pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        changeWorld = pluginInterface.GetIpcSubscriber<string, bool>("Lifestream.ChangeWorld");
        executeCommand = pluginInterface.GetIpcSubscriber<string, object>("Lifestream.ExecuteCommand");
    }

    public bool IsAvailable => isBusy.HasFunction && executeCommand.HasAction;

    public bool IsBusy
    {
        get
        {
            try
            {
                return isBusy.HasFunction && isBusy.InvokeFunc();
            }
            catch
            {
                return false;
            }
        }
    }

    public bool ChangeWorld(string worldName)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(worldName) && changeWorld.HasFunction &&
                   !IsBusy && changeWorld.InvokeFunc(worldName);
        }
        catch
        {
            return false;
        }
    }

    public bool ExecuteCommand(string arguments)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(arguments) || !executeCommand.HasAction || IsBusy)
                return false;
            executeCommand.InvokeAction(arguments.Trim());
            return true;
        }
        catch
        {
            return false;
        }
    }
}
