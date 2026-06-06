using System.Text.Json;
using DashboardApi.Api;
using DashboardApi.Handlers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Reservoir.BuildingBlocks.Persistence;

namespace DashboardApi.Tests;

public class ChaosConfigUpsertHandlerTests
{
    private static (ChaosConfigDbContext db, ChaosConfigUpsertHandler sut, FakeTimeProvider clock) NewSut()
    {
        var options = new DbContextOptionsBuilder<ChaosConfigDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new ChaosConfigDbContext(options);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 6, 12, 0, 0, TimeSpan.Zero));
        return (db, new ChaosConfigUpsertHandler(db, clock), clock);
    }

    [Fact]
    public async Task First_call_inserts_a_new_row()
    {
        var (db, sut, clock) = NewSut();
        var paramsJson = JsonDocument.Parse("""{"delay_ms":5000}""").RootElement;

        var row = await sut.HandleAsync(
            new ChaosConfigSetRequest("delayed_payment", Enabled: true, Params: paramsJson),
            CancellationToken.None);

        row.Name.Should().Be("delayed_payment");
        row.Enabled.Should().BeTrue();
        row.Params.Should().Be("""{"delay_ms":5000}""");
        row.UpdatedAt.Should().Be(clock.GetUtcNow());

        (await db.ChaosConfigs.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Second_call_with_same_name_updates_in_place()
    {
        var (db, sut, clock) = NewSut();
        var initial = JsonDocument.Parse("""{"delay_ms":5000}""").RootElement;
        await sut.HandleAsync(
            new ChaosConfigSetRequest("delayed_payment", Enabled: true, Params: initial),
            CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(30));
        var updated = JsonDocument.Parse("""{"delay_ms":1000}""").RootElement;
        var row = await sut.HandleAsync(
            new ChaosConfigSetRequest("delayed_payment", Enabled: false, Params: updated),
            CancellationToken.None);

        row.Enabled.Should().BeFalse();
        row.Params.Should().Be("""{"delay_ms":1000}""");
        row.UpdatedAt.Should().Be(clock.GetUtcNow());

        (await db.ChaosConfigs.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Null_params_persists_as_empty_object()
    {
        var (_, sut, _) = NewSut();

        var row = await sut.HandleAsync(
            new ChaosConfigSetRequest("noop", Enabled: false, Params: null),
            CancellationToken.None);

        row.Params.Should().Be("{}");
    }

    [Fact]
    public async Task Empty_name_is_rejected()
    {
        var (_, sut, _) = NewSut();

        var act = () => sut.HandleAsync(
            new ChaosConfigSetRequest("  ", Enabled: true, Params: null),
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Non_object_params_is_rejected()
    {
        var (_, sut, _) = NewSut();
        var arrayParams = JsonDocument.Parse("[1,2,3]").RootElement;

        var act = () => sut.HandleAsync(
            new ChaosConfigSetRequest("delayed_payment", Enabled: true, Params: arrayParams),
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
