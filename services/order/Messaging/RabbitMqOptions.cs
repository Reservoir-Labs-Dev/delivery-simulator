namespace OrderService.Messaging;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "reservoir";
    public string Password { get; set; } = "reservoir";
    public string VirtualHost { get; set; } = "/";
    public string Exchange { get; set; } = "orders.exchange";
    public string DeadLetterExchange { get; set; } = "orders.dlx";
}
