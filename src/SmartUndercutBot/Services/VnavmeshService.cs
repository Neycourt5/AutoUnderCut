using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace SmartUndercutBot.Services;

public interface IVnavmeshService
{
    bool IsReady { get; }
    bool IsRunning { get; }
    bool MoveTo(Vector3 destination, float tolerance = 3f);
    void Stop();
}

public sealed class VnavmeshService : IVnavmeshService
{
    private readonly ICallGateSubscriber<bool> isReady;
    private readonly ICallGateSubscriber<bool> isRunning;
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> moveCloseTo;
    private readonly ICallGateSubscriber<bool> stop;

    public VnavmeshService(IDalamudPluginInterface pluginInterface)
    {
        isReady = pluginInterface.GetIpcSubscriber<bool>("Nav.IsReady");
        isRunning = pluginInterface.GetIpcSubscriber<bool>("Path.IsRunning");
        moveCloseTo = pluginInterface.GetIpcSubscriber<Vector3, bool, float, bool>("SimpleMove.PathfindAndMoveCloseTo");
        stop = pluginInterface.GetIpcSubscriber<bool>("Path.Stop");
    }

    public bool IsReady => TryInvoke(isReady);
    public bool IsRunning => TryInvoke(isRunning);

    public bool MoveTo(Vector3 destination, float tolerance = 3f)
    {
        try
        {
            return moveCloseTo.HasFunction && moveCloseTo.InvokeFunc(destination, false, tolerance);
        }
        catch
        {
            return false;
        }
    }

    public void Stop()
    {
        try
        {
            if (stop.HasAction)
                stop.InvokeAction();
        }
        catch
        {
            // Optional dependency can disappear while Dalamud reloads plugins.
        }
    }

    private static bool TryInvoke(ICallGateSubscriber<bool> subscriber)
    {
        try
        {
            return subscriber.HasFunction && subscriber.InvokeFunc();
        }
        catch
        {
            return false;
        }
    }
}
