using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

[Collection("Items projection builder")]
public sealed class ItemEffectsStoreCleanupTests
{
    [Theory]
    [InlineData("reservation")]
    [InlineData("close")]
    [InlineData("name")]
    public async Task ActualBuilderRetainsSlotBuilderAndCreditAfterUncertainStagingRetirement(string fault)
    {
        var f = await ItemEffectsStoreFixture.Create();
        Reservation? reservation = null; ControlledStage? stage = null;
        await f.Store.DisposeAsync();
        f.Store = new(f.H.Catalog, f.H.Objects, f.H.Store, f.H.Tables, f.Resources, TimeProvider.System,
            new(async token => reservation = new(await f.Resources.ReserveStagingAsync(8 << 20, null, token), fault == "reservation"),
                async token =>
                {
                    var input = await f.H.Objects.CreateStagingFileAsync(token); var path = input.Name; await input.DisposeAsync();
                    return stage = new(path, fault);
                }));
        try
        {
            Assert.Equal("ITEM_EFFECTS_CLEANUP_FAILED", (await Assert.ThrowsAsync<DiscoveryException>(() =>
                f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default))).Code);
            Assert.NotNull(stage); Assert.NotNull(reservation);
            Assert.Equal(1, stage.CloseAttempts); Assert.True(stage.CapturedHandle.IsClosed);
            Assert.Equal(fault == "reservation" ? 1 : 0, reservation.Attempts);
            Assert.False(reservation.Released);
            Assert.Equal(1, f.Store.Pressure.BridgeSlots); Assert.True(f.Store.Pressure.Builder);
            Assert.Null(await f.H.Catalog.ReadItemEffectsBridgeAdmissionAsync(f.H.Opening, default));
            Assert.Equal("ITEM_EFFECTS_CLEANUP_FAILED", (await Assert.ThrowsAsync<DiscoveryException>(() => f.Store.ReadAsync(f.Selection, default))).Code);
            Assert.Equal("ITEM_EFFECTS_CLEANUP_FAILED", (await Assert.ThrowsAsync<DiscoveryException>(() => f.Store.DisposeAsync().AsTask())).Code);
            Assert.Equal(1, stage.CloseAttempts);
            Assert.Equal(fault == "reservation" ? 1 : 0, reservation.Attempts);
        }
        finally
        {
            try { await f.Store.DisposeAsync(); } catch (DiscoveryException) { }
            // Test teardown releases the real resource after proving production retained uncertainty;
            // this is not a feature retry or a second invocation of the observed reservation owner.
            reservation?.TestCleanup();
            await f.H.DisposeAsync();
        }
    }

    private sealed class Reservation(IDisposable inner, bool fail) : IDisposable
    {
        internal int Attempts;
        internal bool Released;
        public void Dispose()
        {
            Attempts++; if (fail) throw new IOException("Authored uncertain staging-credit retirement.");
            inner.Dispose(); Released = true;
        }
        internal void TestCleanup() => inner.Dispose();
    }
    private sealed class ControlledStage : FileStream
    {
        private readonly string fault;
        internal readonly Microsoft.Win32.SafeHandles.SafeFileHandle CapturedHandle;
        internal int CloseAttempts;
        internal ControlledStage(string path, string fault) : base(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous)
        { this.fault = fault; CapturedHandle = SafeFileHandle; }
        public override string Name => fault == "name" ? throw new IOException("Authored failed path capture.") : base.Name;
        public override async ValueTask DisposeAsync()
        {
            CloseAttempts++; await base.DisposeAsync();
            if (fault == "close") throw new IOException("Authored uncertain first close.");
        }
    }
}
