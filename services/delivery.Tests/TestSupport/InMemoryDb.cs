using DeliveryService.Data;
using Microsoft.EntityFrameworkCore;

namespace DeliveryService.Tests.TestSupport;

internal static class InMemoryDb
{
    public static DeliveryDbContext Create()
    {
        var options = new DbContextOptionsBuilder<DeliveryDbContext>()
            .UseInMemoryDatabase($"delivery-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new DeliveryDbContext(options);
    }
}
