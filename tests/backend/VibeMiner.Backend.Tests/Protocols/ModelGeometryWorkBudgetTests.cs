using System.Buffers.Binary;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using Xunit;

namespace VibeMiner.Backend.Tests.Protocols;

public sealed class ModelGeometryWorkBudgetTests
{
    [Fact]
    public async Task Legacy_default_literal_and_budgeted_paths_keep_identical_bytes_and_identity()
    {
        var f = await Fixture.Create(); using var legacy = new MemoryStream();
        var original = await ModelGeometryArtifacts.BuildAsync(f.Skin, f.Binding, legacy, default);
        legacy.Position = 0; await ModelGeometryArtifacts.VerifyAsync(legacy, original, default);
        var budget = new RecordingBudget(); using var output = new GuardedStream(budget);
        var current = await WithYieldGuard(budget, () => ModelGeometryArtifacts.BuildAsync(f.Skin, f.Binding, output, budget, default));
        Assert.Equal(legacy.ToArray(), output.Bytes); Assert.Equal(original.ArtifactId, current.ArtifactId);
        Assert.Equal(original.BindingSha256, current.BindingSha256); Assert.Equal(original.Sha256, current.Sha256);
        Assert.Equal(original.ByteLength, current.ByteLength); Assert.Equal(original.Manifest.Bounds.Min, current.Manifest.Bounds.Min);
        using var source = new GuardedStream(budget, output.Bytes, fragment: 7);
        await WithYieldGuard(budget, async () => { await ModelGeometryArtifacts.VerifyAsync(source, current, budget, default); return true; });
        Assert.True(budget.Enters > 10); Assert.True(budget.Yields > 10); Assert.Equal(budget.Enters, budget.Releases); Assert.Equal(0, budget.Active);
        Assert.True(source.CanRead); Assert.True(output.CanWrite);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_before_admission_never_requests_a_scope(bool verify)
    {
        var f = await Fixture.Create(); var (artifact, bytes) = await Build(f); var budget = new RecordingBudget();
        using var stream = new GuardedStream(budget, verify ? bytes : null); using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(verify, f, artifact, stream, budget, stop.Token));
        Assert.Equal(0, budget.Enters); Assert.Equal(0, stream.Reads + stream.Writes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Cancellation_during_grant_retires_a_late_scope_or_a_canceled_wait(bool verify, bool returnLateScope)
    {
        var f = await Fixture.Create(); var (artifact, bytes) = await Build(f);
        var budget = new RecordingBudget { HoldGrant = true, ReturnLateScope = returnLateScope };
        using var stream = new GuardedStream(budget, verify ? bytes : null); using var stop = new CancellationTokenSource();
        var pending = Run(verify, f, artifact, stream, budget, stop.Token);
        await budget.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(0, stream.Reads + stream.Writes);
        stop.Cancel(); budget.Continue.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(returnLateScope ? 1 : 0, budget.Releases); Assert.Equal(0, budget.Active);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_at_every_grant_releases_the_just_granted_scope(bool verify)
    {
        var f = await Fixture.Create(); var (artifact, bytes) = await Build(f); var measured = new RecordingBudget();
        using (var complete = new GuardedStream(measured, verify ? bytes : null)) await Run(verify, f, artifact, complete, measured, default);
        for (var grant = 1; grant <= measured.Enters; grant++)
        {
            using var stop = new CancellationTokenSource(); var budget = new RecordingBudget { CancelAt = grant, Stop = stop };
            using var stream = new GuardedStream(budget, verify ? bytes : null);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(verify, f, artifact, stream, budget, stop.Token));
            Assert.Equal(grant, budget.Enters); Assert.Equal(grant, budget.Releases); Assert.Equal(0, budget.Active);
        }
    }

    [Fact]
    public async Task Held_output_and_fragmented_input_release_cpu_and_preserve_backpressure()
    {
        var f = await Fixture.Create(); var budget = new RecordingBudget(); using var output = new GuardedStream(budget) { HoldWrite = true };
        var pending = ModelGeometryArtifacts.BuildAsync(f.Skin, f.Binding, output, budget, default);
        await output.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(pending.IsCompleted); Assert.Equal(0, budget.Active); Assert.Equal(1, output.Writes);
        output.Continue.TrySetResult(); var artifact = await pending;
        using var input = new GuardedStream(budget, output.Bytes, fragment: 1);
        await ModelGeometryArtifacts.VerifyAsync(input, artifact, budget, default);
        Assert.Equal(artifact.ByteLength, input.BytesRead); Assert.Equal(budget.Enters, budget.Releases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stream_cancellation_and_failures_never_hold_or_leak_work(bool verify)
    {
        var f = await Fixture.Create(); var (artifact, bytes) = await Build(f); using var stop = new CancellationTokenSource();
        var budget = new RecordingBudget(); using var stream = new GuardedStream(budget, verify ? bytes : null) { StopOnIo = stop };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(verify, f, artifact, stream, budget, stop.Token));
        Assert.Equal(0, budget.Active); Assert.Equal(budget.Enters, budget.Releases);
        budget = new(); using var failed = new GuardedStream(budget, verify ? bytes : null) { FailIo = true };
        await Assert.ThrowsAsync<IOException>(() => Run(verify, f, artifact, failed, budget, default));
        Assert.Equal(0, budget.Active); Assert.Equal(budget.Enters, budget.Releases);
    }

    [Fact]
    public async Task Malformed_header_and_late_hash_failure_retire_all_scopes_without_closing_input()
    {
        var f = await Fixture.Create(); var (artifact, bytes) = await Build(f);
        foreach (var at in new[] { 0, 128 + 3 * 12 })
        {
            var bad = bytes.ToArray(); bad[at] ^= 1; var budget = new RecordingBudget(); using var input = new GuardedStream(budget, bad);
            Assert.Equal("GEOMETRY_ARTIFACT_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => ModelGeometryArtifacts.VerifyAsync(input, artifact, budget, default))).Code);
            Assert.Equal(0, budget.Active); Assert.Equal(budget.Enters, budget.Releases); Assert.True(input.CanRead);
        }
    }

    [Fact]
    public async Task Large_overlapping_sections_have_bounded_serialization_and_repeated_cpu_turns()
    {
        var f = await Fixture.Create(large: true); var budget = new RecordingBudget(); using var output = new GuardedStream(budget);
        var artifact = await WithYieldGuard(budget, () => ModelGeometryArtifacts.BuildAsync(f.Skin, f.Binding, output, budget, default));
        // Includes full value-struct indexer allowances, not just the selected XYZ/scalar fields.
        Assert.InRange(output.MaximumWrite, 1, 65536 / 100 * 24);
        var positionWrites = output.WriteRanges.Where(r => r.Start >= 128 && r.Start < artifact.Manifest.LineIndices.Offset).ToArray();
        Assert.Equal((artifact.Manifest.VertexCount + 65536 / 60 - 1) / (65536 / 60), positionWrites.Length);
        Assert.All(positionWrites, r => Assert.InRange(r.Count, 1, 65536 / 60 * 12));
        Assert.True(budget.Enters > 60); Assert.True(budget.Yields >= budget.Enters - 1);
        var prior = budget.Enters; using var input = new GuardedStream(budget, output.Bytes);
        await WithYieldGuard(budget, async () => { await ModelGeometryArtifacts.VerifyAsync(input, artifact, budget, default); return true; });
        Assert.True(budget.Enters - prior > 60); Assert.InRange(input.MaximumRead, 1, 65536);
        Assert.Equal(artifact.ByteLength, input.BytesRead); Assert.Equal(budget.Enters, budget.Releases); Assert.Equal(0, budget.Active);
        var (legacy, bytes) = await Build(f); Assert.Equal(legacy.ArtifactId, artifact.ArtifactId); Assert.Equal(bytes, output.Bytes);
    }

    private static Task Run(bool verify, Fixture f, GeometryArtifact artifact, Stream stream, RecordingBudget budget, CancellationToken token) =>
        verify ? ModelGeometryArtifacts.VerifyAsync(stream, artifact, budget, token) : ModelGeometryArtifacts.BuildAsync(f.Skin, f.Binding, stream, budget, token);
    private static async Task<(GeometryArtifact, byte[])> Build(Fixture f)
    { using var output = new MemoryStream(); var artifact = await ModelGeometryArtifacts.BuildAsync(f.Skin, f.Binding, output); return (artifact, output.ToArray()); }

    private static async Task<T> WithYieldGuard<T>(RecordingBudget budget, Func<Task<T>> action)
    {
        var previous = SynchronizationContext.Current; Task<T> pending;
        try { SynchronizationContext.SetSynchronizationContext(new YieldGuard(previous, budget)); pending = action(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        return await pending;
    }
    private sealed class YieldGuard(SynchronizationContext? parent, RecordingBudget budget) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
        {
            Assert.Equal(0, budget.Active); budget.Yields++;
            (parent ?? new SynchronizationContext()).Post(_ =>
            {
                var prior = Current; SetSynchronizationContext(this);
                try { callback(state); } finally { SetSynchronizationContext(prior); }
            }, null);
        }
    }
    private sealed class RecordingBudget : IGeometryArtifactWorkBudget
    {
        public int Active, Enters, Releases, Yields, CancelAt = int.MaxValue;
        public bool HoldGrant, ReturnLateScope;
        public CancellationTokenSource? Stop;
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<IDisposable> EnterAsync(CancellationToken token)
        {
            Assert.Equal(0, Active); Enters++;
            if (HoldGrant) { Waiting.TrySetResult(); await Continue.Task; if (!ReturnLateScope) token.ThrowIfCancellationRequested(); }
            Active++; if (Enters == CancelAt) Stop!.Cancel(); return new Scope(this);
        }
        private sealed class Scope(RecordingBudget owner) : IDisposable
        { public void Dispose() { Assert.Equal(1, owner.Active); owner.Active--; owner.Releases++; } }
    }
    private sealed class GuardedStream(RecordingBudget budget, byte[]? bytes = null, int fragment = 65536) : Stream
    {
        private readonly MemoryStream inner = bytes is null ? new() : new(bytes);
        public int Reads, Writes, MaximumWrite, MaximumRead; public long BytesRead;
        public List<(long Start, int Count)> WriteRanges { get; } = [];
        public bool HoldWrite, FailIo; public CancellationTokenSource? StopOnIo;
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private void Guard() => Assert.Equal(0, budget.Active);
        public byte[] Bytes { get { Guard(); return inner.ToArray(); } }
        public override bool CanRead { get { Guard(); return inner.CanRead; } }
        public override bool CanWrite { get { Guard(); return inner.CanWrite; } }
        public override bool CanSeek { get { Guard(); return true; } }
        public override long Length { get { Guard(); return inner.Length; } }
        public override long Position { get { Guard(); return inner.Position; } set { Guard(); inner.Position = value; } }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            Guard(); Writes++; WriteRanges.Add((inner.Position, buffer.Length)); MaximumWrite = Math.Max(MaximumWrite, buffer.Length); StopOnIo?.Cancel(); token.ThrowIfCancellationRequested();
            if (FailIo) throw new IOException("Authored write failure.");
            if (HoldWrite && Writes == 1) { Waiting.TrySetResult(); await Continue.Task.WaitAsync(token); Guard(); }
            await inner.WriteAsync(buffer, token);
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            Guard(); Reads++; MaximumRead = Math.Max(MaximumRead, buffer.Length); StopOnIo?.Cancel(); token.ThrowIfCancellationRequested();
            if (FailIo) throw new IOException("Authored read failure.");
            var count = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, fragment)], token); BytesRead += count; return count;
        }
        public override void Flush() { Guard(); inner.Flush(); }
        public override long Seek(long offset, SeekOrigin origin) { Guard(); return inner.Seek(offset, origin); }
        public override void SetLength(long value) { Guard(); inner.SetLength(value); }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous read forbidden.");
        public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous write forbidden.");
        protected override void Dispose(bool disposing) { Guard(); if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    // Authored MD21/SKIN bytes only, with an unreferenced fourth vertex for late-hash mutation.
    private sealed record Fixture(M2GeometrySkin Skin, GeometrySourceBinding Binding)
    {
        public static async Task<Fixture> Create(bool large = false)
        {
            var vertices = large ? 65536 : 4; var indexCount = large ? 65535 : 3;
            var body = new byte[304 + vertices * 48]; "MD20"u8.CopyTo(body); Put(body, 4, 274); Put(body, 60, (uint)vertices); Put(body, 64, 304); Put(body, 68, 1);
            for (var i = 0; i < vertices; i++)
            { BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(304 + i * 48), i); BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(304 + i * 48 + 28), 1); }
            var modelBytes = new byte[body.Length + 20]; "MD21"u8.CopyTo(modelBytes); Put(modelBytes, 4, (uint)body.Length); body.CopyTo(modelBytes, 8);
            "SFID"u8.CopyTo(modelBytes.AsSpan(body.Length + 8)); Put(modelBytes, body.Length + 12, 4); Put(modelBytes, body.Length + 16, 900);
            using var modelSource = new MemoryStream(modelBytes); var model = await M2ModelReader.ReadAsync(modelSource);
            var sectionsAt = 56 + 6 + indexCount * 2 + 12; var sectionCount = large ? 2 : 1;
            var skinBytes = new byte[sectionsAt + sectionCount * 48]; "SKIN"u8.CopyTo(skinBytes); Put(skinBytes, 4, 3); Put(skinBytes, 8, 56);
            Put(skinBytes, 12, (uint)indexCount); Put(skinBytes, 16, 62); Put(skinBytes, 20, 3); Put(skinBytes, 24, (uint)(62 + indexCount * 2));
            Put(skinBytes, 28, (uint)sectionCount); Put(skinBytes, 32, (uint)sectionsAt);
            for (var i = 0; i < 3; i++) BinaryPrimitives.WriteUInt16LittleEndian(skinBytes.AsSpan(56 + i * 2), (ushort)i);
            for (var i = 0; i < indexCount; i++) BinaryPrimitives.WriteUInt16LittleEndian(skinBytes.AsSpan(62 + i * 2), (ushort)(i % 3));
            for (var i = 0; i < sectionCount; i++)
            { BinaryPrimitives.WriteUInt16LittleEndian(skinBytes.AsSpan(sectionsAt + i * 48 + 6), 3); BinaryPrimitives.WriteUInt16LittleEndian(skinBytes.AsSpan(sectionsAt + i * 48 + 10), (ushort)indexCount); }
            using var skinSource = new MemoryStream(skinBytes); var skin = await M2GeometrySkinReader.ReadAsync(skinSource, model);
            return new(skin, new(new(new string('1', 32), new string('2', 32), new string('3', 64), 100, modelBytes.Length, "content-blte-v1"),
                new(new string('4', 32), new string('5', 32), new string('6', 64), 80, skinBytes.Length, "content-blte-v1"), 800, 900, 0));
        }
        private static void Put(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
    }
}
