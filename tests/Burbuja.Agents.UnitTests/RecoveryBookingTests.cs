using Burbuja.Agents.Abstractions.Budget;
using Burbuja.Agents.Runtime;
using Burbuja.Core.Arm;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Burbuja.Agents.UnitTests;

public sealed class RecoveryBookingTests
{
    [Fact]
    public async Task A_recorded_charge_is_not_booked_twice_when_a_recovered_response_uses_its_original_identity_and_day()
    {
        using var host = Harness.CreateHost();
        var ledger = host.GetRequiredService<IBudgetLedger>();
        var id = Guid.NewGuid();
        var job = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 1, 1, 23, 59, 59, TimeSpan.Zero);
        var booking = new BudgetBooking(job, Guid.NewGuid(), BudgetKind.Worker, 1m, 10, 5)
        { IdempotencyId = id, BookedAt = at };
        Assert.Equal(id, await ledger.BookAsync(booking, TestContext.Current.CancellationToken));
        Assert.Equal(id, await ledger.BookAsync(booking, TestContext.Current.CancellationToken));
        Assert.Equal(1m, await ledger.GetJobSpendAsync(job, TestContext.Current.CancellationToken));
        Assert.Equal(1m, (await ledger.GetDailyAsync(new DateOnly(2026, 1, 1), TestContext.Current.CancellationToken)).SpentUsd);
        Assert.Equal(0m, (await ledger.GetDailyAsync(new DateOnly(2026, 1, 2), TestContext.Current.CancellationToken)).SpentUsd);
    }

    [Fact]
    public async Task Reusing_a_charge_identity_for_a_different_amount_is_refused()
    {
        using var host = Harness.CreateHost();
        var ledger = host.GetRequiredService<IBudgetLedger>();
        var booking = new BudgetBooking(Guid.NewGuid(), Guid.NewGuid(), BudgetKind.Worker, 1m, 10, 5)
        { IdempotencyId = Guid.NewGuid(), BookedAt = DateTimeOffset.UnixEpoch };
        await ledger.BookAsync(booking, TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<ArmException>(async () =>
            await ledger.BookAsync(booking with { Usd = 2m }, TestContext.Current.CancellationToken));
        Assert.Equal("BookingIdentityConflict", error.Error.Code);
    }
}
