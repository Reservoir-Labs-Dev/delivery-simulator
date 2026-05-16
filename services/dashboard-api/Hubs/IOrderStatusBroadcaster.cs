using Reservoir.BuildingBlocks.Contracts;

namespace DashboardApi.Hubs;

/// <summary>
/// Pushes status notifications to all connected SignalR clients.
/// Abstracted behind an interface so handlers can be unit-tested against a
/// fake (see <c>FakeOrderStatusBroadcaster</c> in the test project) instead
/// of the real SignalR plumbing.
/// </summary>
public interface IOrderStatusBroadcaster
{
    Task BroadcastAsync(OrderStatusChangedNotification notification, CancellationToken ct);
}
