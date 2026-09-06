using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace SmartUndercutBot.Services;

public interface ILifestreamService
{
    bool IsAvailable { get; }
    bool IsBusy { get; }
    bool ChangeWorld(string worldName);
    void Abort();
}

public sealed class LifestreamService : ILifestreamService
{
    private readonly ICallGateSubscriber<bool> isBusy;
    private readonly ICallGateSubscriber<string, bool> changeWorld;
    private readonly ICallGateSubscriber<object> abort;

    public LifestreamService(IDalamudPluginInterface pluginInterface)
    {
        isBusy = pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        changeWorld = pluginInterface.GetIpcSubscriber<string, bool>("Lifestream.ChangeWorld");
        abort = pluginInterface.GetIpcSubscriber<object>("Lifestream.Abort");
    }

    public bool IsAvailable => isBusy.HasFunction && changeWorld.HasFunction;

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

    public void Abort()
    {
        try
        {
            if (abort.HasAction)
                abort.InvokeAction();
        }
        catch
        {
            // The optional plugin may have been unloaded.
        }
    }

}
