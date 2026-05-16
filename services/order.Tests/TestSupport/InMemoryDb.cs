using Microsoft.EntityFrameworkCore;
using OrderService.Data;

namespace OrderService.Tests.TestSupport;

internal static class InMemoryDb
{
    public static OrdersDbContext Create()
    {
        var options = new DbContextOptionsBuilder<OrdersDbContext>()
            .UseInMemoryDatabase($"orders-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new OrdersDbContext(options);
    }
}
