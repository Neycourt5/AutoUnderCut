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
        public object?[]? TravelArguments { get; set; }
        public bool GatewayAvailable { get; set; } = true;
        public ICallGateSubscriber<T1, T2, T3, T4, T5, T6, T7, T> GetIpcSubscriber<T1, T2, T3, T4, T5, T6, T7, T>(string name)
        {
            Names.Add(name);
            Assert.Equal("Lifestream.TPAndChangeWorld", name);
            return new Gateway<T1, T2, T3, T4, T5, T6, T7, T>(this);
        }
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
    private sealed class Gateway<T1, T2, T3, T4, T5, T6, T7, T>(Ipc ipc) : ICallGateSubscriber<T1, T2, T3, T4, T5, T6, T7, T>
    {
        public bool HasAction => ipc.GatewayAvailable;
        public void InvokeAction(T1 a, T2 b, T3 c, T4 d, T5 e, T6 f, T7 g) => ipc.TravelArguments = [a, b, c, d, e, f, g];
    }

    [Fact]
    public void WorldTravelExplicitlyUsesLimsaAndReturnsToItsGateway()
    {
        var ipc = new Ipc();
        var travel = new LifestreamService(ipc);
        Assert.True(travel.TryChangeWorldViaLimsa("Behemoth", true));
        Assert.Equal(new object?[] { "Behemoth", true, "", true, 8, false, true }, ipc.TravelArguments);
        ipc.GatewayAvailable = false;
        Assert.False(travel.TryChangeWorldViaLimsa("Behemoth", true));
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
