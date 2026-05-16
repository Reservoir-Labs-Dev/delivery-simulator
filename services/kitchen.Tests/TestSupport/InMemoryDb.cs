using KitchenService.Data;
using Microsoft.EntityFrameworkCore;

namespace KitchenService.Tests.TestSupport;

internal static class InMemoryDb
{
    public static KitchenDbContext Create()
    {
        var options = new DbContextOptionsBuilder<KitchenDbContext>()
            .UseInMemoryDatabase($"kitchen-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new KitchenDbContext(options);
    }
}
