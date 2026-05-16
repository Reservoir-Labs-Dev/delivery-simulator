using Microsoft.AspNetCore.SignalR;
using Reservoir.BuildingBlocks.Contracts;

namespace DashboardApi.Hubs;

/// <summary>
/// SignalR hub the frontend connects to for live order updates.
///
/// The hub does not accept any commands from clients — it is a one-way
/// fan-out from <c>dashboard-api</c> to all connected browsers. Status
/// notifications arrive via <see cref="IOrderStatusBroadcaster"/>
/// (implemented by <see cref="SignalROrderStatusBroadcaster"/>), which
/// invokes <c>Clients.All.SendAsync("OrderStatusChanged", n)</c>.
///
/// Frontend method name: <c>OrderStatusChanged</c>, single argument of
/// shape <see cref="OrderStatusChangedNotification"/>.
/// </summary>
public sealed class OrdersHub : Hub
{
    public const string Path = "/hubs/orders";
    public const string ClientMethod = "OrderStatusChanged";
}
