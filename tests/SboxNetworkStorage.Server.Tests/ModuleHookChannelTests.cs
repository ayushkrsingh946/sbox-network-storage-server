using SboxNetworkStorage.Infrastructure.NetworkStorage.Modules;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>Hook dispatch channel: bounded, non-blocking, drop-counted.</summary>
public sealed class ModuleHookChannelTests
{
    [Fact]
    public void Emission_accepts_events_without_blocking_until_full_then_counts_drops()
    {
        var dispatcher = new ModuleHookDispatcher();
        var accepted = 0;
        for (var i = 0; i < ModuleHookDispatcher.Capacity; i++)
        {
            if (dispatcher.TryEmit(ModuleHookDispatcher.Describe("endpoint.executed", "game",
                DateTimeOffset.UtcNow, steamId: "p1")))
                accepted++;
        }
        Assert.Equal(ModuleHookDispatcher.Capacity, accepted);
        Assert.Equal(0, dispatcher.DroppedEvents);
        Assert.False(dispatcher.TryEmit(ModuleHookDispatcher.Describe("endpoint.executed", "game",
            DateTimeOffset.UtcNow, steamId: "p1")));
        Assert.False(dispatcher.TryEmit(ModuleHookDispatcher.Describe("endpoint.executed", "game",
            DateTimeOffset.UtcNow, steamId: "p1")));
        Assert.Equal(2, dispatcher.DroppedEvents);
    }

    [Fact]
    public void Slow_subscribers_never_see_events_without_a_running_service()
    {
        // Emission is fire-and-forget: filling the channel proves the request
        // path never waits on subscribers (there is no reader here at all).
        var dispatcher = new ModuleHookDispatcher();
        for (var i = 0; i < 100; i++)
            Assert.True(dispatcher.TryEmit(ModuleHookDispatcher.Describe("heartbeat", "game", DateTimeOffset.UtcNow)));
        Assert.Equal(0, dispatcher.DroppedEvents);
    }
}
