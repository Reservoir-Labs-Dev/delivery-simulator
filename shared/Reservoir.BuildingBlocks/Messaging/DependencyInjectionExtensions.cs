using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Reservoir.BuildingBlocks.Messaging;

public static class DependencyInjectionExtensions
{
    /// <summary>
    /// Binds <see cref="RabbitMqOptions"/> from <c>RabbitMq</c> config section and
    /// registers <see cref="RabbitMqEventPublisher"/> as the singleton
    /// <see cref="IEventPublisher"/>.
    /// </summary>
    public static IServiceCollection AddRabbitMqPublisher(
        this IServiceCollection services,
        IConfiguration configuration,
        string? sectionName = null)
    {
        services.Configure<RabbitMqOptions>(
            configuration.GetSection(sectionName ?? RabbitMqOptions.SectionName));

        services.AddSingleton<IEventPublisher, RabbitMqEventPublisher>();
        return services;
    }
}
