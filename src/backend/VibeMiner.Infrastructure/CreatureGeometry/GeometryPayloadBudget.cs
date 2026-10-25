using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

internal interface IGeometryPayloadBudget { IDisposable Reserve(long bytes); }

/// <summary>One composition-owned immediate ledger shared by producing and all read/lease debt.
/// It is a reservation ceiling, not an assertion about process RSS or a second work scheduler.</summary>
internal sealed class GeometryPayloadBudget : IGeometryPayloadBudget
{
    // Producer: 16 MiB capture + 32 MiB sources + 16 MiB decoded textures (#63); two 8 MiB read slots.
    internal const long MaximumBytes = 80L << 20, ProducerBytes = 64L << 20, ReaderBytes = 8L << 20;
    private readonly object gate = new();
    private long used;
    internal long ReservedBytes { get { lock (gate) return used; } }
    internal GeometryProducerPayload ReserveProducer() => new(Reserve(ProducerBytes));
    public IDisposable Reserve(long bytes)
    {
        if (bytes is < 1 or > MaximumBytes) throw Limit();
        lock (gate)
        {
            if (used > MaximumBytes - bytes) throw Limit();
            used += bytes;
        }
        return new Reservation(this, bytes);
    }
    private sealed class Reservation(GeometryPayloadBudget owner, long bytes) : IDisposable
    {
        private GeometryPayloadBudget? active = owner;
        public void Dispose()
        { var value = Interlocked.Exchange(ref active, null); if (value is not null) lock (value.gate) value.used -= bytes; }
    }
    private static DiscoveryException Limit() => new("CREATURE_GEOMETRY_RESOURCE_LIMIT", "Geometry payload reservations are at their bounded capacity.", true);
}


/// <summary>The producer's local subledger; the enclosing global48MiB is charged once.</summary>
internal sealed class GeometryProducerPayload(IDisposable global) : IGeometryPayloadBudget, IDisposable
{
    private readonly object gate = new();
    private long used; private int entries; private bool closed;
    private IDisposable? reservation = global;
    public IDisposable Reserve(long bytes)
    {
        lock (gate)
        {
            if (closed) throw new ObjectDisposedException(nameof(GeometryProducerPayload));
            if (bytes < 1 || bytes > GeometryPayloadBudget.ProducerBytes - used || entries >= 4)
                throw new DiscoveryException("CREATURE_GEOMETRY_RESOURCE_LIMIT", "The geometry producer's complete payload exceeds its allowance.");
            used += bytes; entries++; return new Child(this, bytes);
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            closed = true;
            if (entries != 0) throw new DiscoveryException("CREATURE_GEOMETRY_CLEANUP_FAILED", "Geometry producer payload is still owned; its global reservation remains charged.");
            Interlocked.Exchange(ref reservation, null)?.Dispose();
        }
    }
    private sealed class Child(GeometryProducerPayload owner, long bytes) : IDisposable
    {
        private GeometryProducerPayload? active = owner;
        public void Dispose()
        {
            var value = Interlocked.Exchange(ref active, null);
            if (value is not null) lock (value.gate)
            {
                value.used -= bytes; value.entries--;
                if (value.closed && value.entries == 0) Interlocked.Exchange(ref value.reservation, null)?.Dispose();
            }
        }
    }
}
