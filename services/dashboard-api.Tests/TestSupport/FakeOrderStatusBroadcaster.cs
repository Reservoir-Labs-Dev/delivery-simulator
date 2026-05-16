using System.Collections.Concurrent;
using DashboardApi.Hubs;
using Reservoir.BuildingBlocks.Contracts;

namespace DashboardApi.Tests.TestSupport;

internal sealed class FakeOrderStatusBroadcaster : IOrderStatusBroadcaster
{
    private readonly ConcurrentBag<OrderStatusChangedNotification> _broadcasts = new();
    private readonly ManualResetEventSlim _signal = new(initialState: false);

    public IReadOnlyCollection<OrderStatusChangedNotification> Broadcasts => _broadcasts;

    public Task BroadcastAsync(OrderStatusChangedNotification notification, CancellationToken ct)
    {
        _broadcasts.Add(notification);
        _signal.Set();
        return Task.CompletedTask;
    }

    public bool Wait(TimeSpan timeout) => _signal.Wait(timeout);

    public void Reset()
    {
        _broadcasts.Clear();
        _signal.Reset();
    }
}
