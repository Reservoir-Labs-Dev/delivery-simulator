using RabbitMQ.Client;
using Reservoir.BuildingBlocks.Messaging;

namespace Reservoir.TestSupport;

public static class BrokerProbe
{
    /// <summary>
    /// Returns true if a RabbitMQ broker is reachable at the configured host/port.
    /// Uses a 2-second timeout so the suite stays fast when the broker is down.
    /// </summary>
    public static bool IsAvailable(RabbitMqOptions options, string clientName = "test-probe")
    {
        try
        {
            var factory = new ConnectionFactory
            {
                HostName = options.HostName,
                Port = options.Port,
                UserName = options.UserName,
                Password = options.Password,
                VirtualHost = options.VirtualHost,
                RequestedConnectionTimeout = TimeSpan.FromSeconds(2),
                SocketReadTimeout = TimeSpan.FromSeconds(2),
                SocketWriteTimeout = TimeSpan.FromSeconds(2),
                ClientProvidedName = clientName,
            };
            using var conn = factory.CreateConnection();
            return conn.IsOpen;
        }
        catch
        {
            return false;
        }
    }
}
