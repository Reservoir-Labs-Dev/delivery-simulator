using Microsoft.EntityFrameworkCore;
using PaymentService.Data;

namespace PaymentService.Tests.TestSupport;

internal static class InMemoryDb
{
    public static PaymentsDbContext Create()
    {
        var options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseInMemoryDatabase($"payments-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new PaymentsDbContext(options);
    }
}
