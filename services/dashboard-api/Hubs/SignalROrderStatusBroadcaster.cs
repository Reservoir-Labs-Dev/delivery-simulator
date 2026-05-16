using Microsoft.AspNetCore.SignalR;
using Reservoir.BuildingBlocks.Contracts;

namespace DashboardApi.Hubs;

public sealed class SignalROrderStatusBroadcaster : IOrderStatusBroadcaster
{
    private readonly IHubContext<OrdersHub> _hub;

    public SignalROrderStatusBroadcaster(IHubContext<OrdersHub> hub) => _hub = hub;

    public Task BroadcastAsync(OrderStatusChangedNotification notification, CancellationToken ct) =>
        _hub.Clients.All.SendAsync(OrdersHub.ClientMethod, notification, ct);
}
