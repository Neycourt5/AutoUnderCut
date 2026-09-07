using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace SmartUndercutBot.Services;

public interface ILifestreamService
{
    bool IsAvailable { get; }
    bool IsBusy { get; }
    bool ChangeWorld(string worldName);
    bool TryChangeWorldViaLimsa(string worldName, bool crossDataCenter) => false;
    void Abort();
}

public sealed class LifestreamService : ILifestreamService
{
    private readonly ICallGateSubscriber<bool> isBusy;
    private readonly ICallGateSubscriber<string, bool> changeWorld;
    private readonly ICallGateSubscriber<object> abort;
    private readonly ICallGateSubscriber<string, bool, string, bool, int?, bool?, bool?, object> travelViaGateway;

    public LifestreamService(IDalamudPluginInterface pluginInterface)
    {
        isBusy = pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        changeWorld = pluginInterface.GetIpcSubscriber<string, bool>("Lifestream.ChangeWorld");
        abort = pluginInterface.GetIpcSubscriber<object>("Lifestream.Abort");
        travelViaGateway = pluginInterface.GetIpcSubscriber<string, bool, string, bool, int?, bool?, bool?, object>("Lifestream.TPAndChangeWorld");
    }

    public bool IsAvailable => isBusy.HasFunction && changeWorld.HasFunction;

    public bool TryChangeWorldViaLimsa(string worldName, bool crossDataCenter)
    {
        if (!travelViaGateway.HasAction || IsBusy || string.IsNullOrWhiteSpace(worldName)) return false;
        try
        {
            // Lifestream's public gateway argument: Limsa aetheryte ID 8.
            // Explicitly return to the gateway after cross-DC login.
            travelViaGateway.InvokeAction(worldName, crossDataCenter, string.Empty, true, 8, false, true);
            return true;
        }
        catch { return false; }
    }

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
