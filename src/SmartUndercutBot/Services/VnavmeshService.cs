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
    private readonly ICallGateSubscriber<bool> pathfindInProgress;
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> moveCloseTo;
    private readonly ICallGateSubscriber<object> stop;
    private readonly ICallGateSubscriber<object> cancelPathfind;

    public VnavmeshService(IDalamudPluginInterface pluginInterface)
    {
        isReady = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        isRunning = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        pathfindInProgress = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        moveCloseTo = pluginInterface.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        stop = pluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        cancelPathfind = pluginInterface.GetIpcSubscriber<object>("vnavmesh.Nav.PathfindCancelAll");
    }

    public bool IsReady => TryInvoke(isReady);
    public bool IsRunning => TryInvoke(isRunning) || TryInvoke(pathfindInProgress);

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
            // Stopping only the current path lets a pending async path start
            // moving the character again after the route was cancelled.
            if (TryInvoke(pathfindInProgress) && cancelPathfind.HasAction)
                cancelPathfind.InvokeAction();
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
