using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using SmartUndercutBot.Services;
using Xunit;

namespace SmartUndercutBot.Core.Tests.Automation;

public sealed class TravelIpcTests
{
    [Fact]
    public void NavigationUsesPublishedEndpointsAndCountsPendingPathfindingAsRunning()
    {
        var ipc = new Ipc();
        var nav = new VnavmeshService(ipc);
        Assert.True(nav.IsReady);
        Assert.True(nav.MoveTo(Vector3.One));
        Assert.False(nav.IsRunning);
        ipc.Pathfinding = true;
        Assert.True(nav.IsRunning);
        nav.Stop();
        Assert.Equal(new[] { "vnavmesh.Nav.PathfindCancelAll", "vnavmesh.Path.Stop" }, ipc.Actions);
        Assert.All(ipc.Names, name => Assert.StartsWith("vnavmesh.", name));
    }

    [Fact]
    public void LifestreamAbortCallsPublishedAction()
    {
        var ipc = new Ipc();
        var travel = new LifestreamService(ipc);
        Assert.True(travel.IsAvailable);
        travel.Abort();
        Assert.Equal("Lifestream.Abort", Assert.Single(ipc.Actions));
    }

    private sealed class Ipc : IDalamudPluginInterface
    {
        public bool Pathfinding { get; set; }
        public List<string> Names { get; } = [];
        public List<string> Actions { get; } = [];
        public ICallGateSubscriber<T> GetIpcSubscriber<T>(string name)
        {
            Names.Add(name);
            return new Gate<T>(() => (T)(object)(name switch
            {
                "vnavmesh.Nav.IsReady" => true,
                "vnavmesh.SimpleMove.PathfindInProgress" => Pathfinding,
                _ => false,
            }), () => Actions.Add(name));
        }
        public ICallGateSubscriber<T1, T> GetIpcSubscriber<T1, T>(string name)
        {
            Names.Add(name);
            return new Gate<T1, T>();
        }
        public ICallGateSubscriber<T1, T2, T3, T> GetIpcSubscriber<T1, T2, T3, T>(string name)
        {
            Names.Add(name);
            Assert.Equal("vnavmesh.SimpleMove.PathfindAndMoveCloseTo", name);
            return new Gate<T1, T2, T3, T>();
        }
    }
    private sealed class Gate<T>(Func<T> function, Action action) : ICallGateSubscriber<T>
    {
        public bool HasFunction => true;
        public bool HasAction => true;
        public T InvokeFunc() => function();
        public void InvokeAction() => action();
    }
    private sealed class Gate<T1, T> : ICallGateSubscriber<T1, T>
    {
        public bool HasFunction => true;
        public T InvokeFunc(T1 arg) => (T)(object)true;
    }
    private sealed class Gate<T1, T2, T3, T> : ICallGateSubscriber<T1, T2, T3, T>
    {
        public bool HasFunction => true;
        public T InvokeFunc(T1 a, T2 b, T3 c) => (T)(object)true;
    }
}
