using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using Xunit;
using Xunit.Abstractions;

namespace VibeMiner.Backend.Tests.Protocols;

public sealed class ModelGeometryArtifactTests(ITestOutputHelper testOutput)
{
    [Fact]
    public async Task Authored_golden_keeps_native_bits_all_section_order_and_unused_vertex_bounds()
    {
        var f = await Fixture.Create(); using var output = new MemoryStream(); var a = await ModelGeometryArtifacts.BuildAsync(f.Skin, f.Binding, output); var b = output.ToArray();
        Assert.Equal(320, b.Length); Assert.Equal("VMG1", Encoding.ASCII.GetString(b, 0, 4)); Assert.Equal(1, W(b, 4)); Assert.Equal(128, W(b, 6));
        Assert.Equal(new uint[] { 4, 12, 3, 3 }, Enumerable.Range(0, 4).Select(i => U(b, 48 + i * 4)));
        Assert.Equal(new uint[] { 128, 48, 176, 48, 224, 96 }, Enumerable.Range(0, 6).Select(i => U(b, 64 + i * 4)));
        Assert.Equal(int.MinValue, BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(128))); // Original -0, not normalized positions.
        Assert.Equal(new uint[] { 2, 0, 0, 1, 1, 2, 2, 0, 0, 1, 1, 2 }, Enumerable.Range(0, 12).Select(i => U(b, 176 + i * 4)));
        Assert.Equal(new float[] { -6, -4, 0 }, a.Manifest.Bounds.Min); Assert.Equal(new float[] { 3, 7, 8 }, a.Manifest.Bounds.Max);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(96))); // Canonical header zero.
        Assert.Equal(3, a.Manifest.SectionCount); Assert.Equal(6, a.Manifest.SourceTriangleIndexCount); Assert.Equal(6, a.Manifest.SectionIndexCount);
        Assert.Equal(new uint[] { 0, 6, 12 }, Enumerable.Range(0, 3).Select(i => U(b, 224 + i * 32 + 24)));
        Assert.Equal(0u, U(b, 224 + 2 * 32 + 28)); Assert.Equal("notEvaluated", a.Manifest.Evaluations.Appearance);
        // Independently calculated frozen SHA goldens (authored fixture only).
        Assert.Equal("26d091dddb2aae81502176c74454a201d3c55e29150aa0f3d3311d19a08cc956", a.BindingSha256); Assert.Equal("7c6551ca0fe903121e78b848a221f72f504048b9a5ba606576f07d8b897f1119", a.Sha256); Assert.Equal("04d7492b51e1ba4ec7d84829ff5bc79a1bd54cdc9c38dc5eddb6682eb143dc45", a.ArtifactId);
        output.Position = 0; await ModelGeometryArtifacts.VerifyAsync(output, a); Assert.True(output.CanRead);
        using var again = new MemoryStream(); var other = await ModelGeometryArtifacts.BuildAsync(f.Skin, f.Binding, again);
        Assert.Equal(a.ArtifactId, other.ArtifactId); Assert.Equal(b, again.ToArray());
        Assert.Throws<NotSupportedException>(() => ((IList<float>)a.Manifest.Bounds.Min)[0] = 7);
    }

    [Theory]
    [InlineData("sourceLength")][InlineData("skinLength")][InlineData("main")][InlineData("ordinal")]
    [InlineData("zeroId")][InlineData("key")][InlineData("uppercase")][InlineData("verifier")]
    [InlineData("zeroLength")][InlineData("encodedLimit")][InlineData("decodedLimit")]
    public async Task Source_shape_and_exact_typed_association_refuse_before_output(string kind)
    {
        var f = await Fixture.Create(); var binding = f.Binding;
        binding = kind switch
        {
            "sourceLength" => binding with { Model = binding.Model with { DecodedBytes = binding.Model.DecodedBytes + 1 } },
            "skinLength" => binding with { Skin = binding.Skin with { DecodedBytes = binding.Skin.DecodedBytes + 1 } },
            "main" => binding with { SkinFileDataId = 901 }, "ordinal" => binding with { MainSkinOrdinal = 1 },
            "zeroId" => binding with { ModelFileDataId = 0 }, "key" => binding with { Model = binding.Model with { ContentKey = "bad" } },
            "uppercase" => binding with { Model = binding.Model with { ContentKey = new string('A', 32) } },
            "verifier" => binding with { Model = binding.Model with { VerifierRevision = "content-blte-projection-v1" } },
            "zeroLength" => binding with { Model = binding.Model with { EncodedBytes = 0 } },
            "encodedLimit" => binding with { Model = binding.Model with { EncodedBytes = (4L << 20) + 1 } },
            _ => binding with { Skin = binding.Skin with { DecodedBytes = (8L << 20) + 1 } }
        };
        using var output = new TrackedOutput(); await Error(kind.EndsWith("Limit", StringComparison.Ordinal) ? "LIMIT" : "INVALID", () => ModelGeometryArtifacts.BuildAsync(f.Skin, binding, output));
        Assert.Equal(0, output.Writes); Assert.Equal(0, output.Length); Assert.True(output.CanWrite);
    }

    [Fact]
    public async Task Each_immutable_source_identity_changes_binding_and_full_artifact_but_not_native_positions()
    {
        var f = await Fixture.Create(); var (a, bytes) = await Build(f);
        var candidates = new[] { f.Binding with { ModelFileDataId = 801 }, f.Binding with { Model = f.Binding.Model with { EncodedSha256 = new string('7', 64) } },
            f.Binding with { Skin = f.Binding.Skin with { ContentKey = new string('8', 32) } }, f.Binding with { Model = f.Binding.Model with { EncodedBytes = 101 } } };
        foreach (var binding in candidates)
        { using var output = new MemoryStream(); var changed = await ModelGeometryArtifacts.BuildAsync(f.Skin, binding, output); Assert.NotEqual(a.BindingSha256, changed.BindingSha256); Assert.NotEqual(a.Sha256, changed.Sha256); Assert.Equal(bytes[128..], output.ToArray()[128..]); }
    }

    [Theory]
    [InlineData(0)][InlineData(4)][InlineData(6)][InlineData(8)][InlineData(12)][InlineData(16)]
    [InlineData(48)][InlineData(52)][InlineData(56)][InlineData(60)][InlineData(64)][InlineData(68)]
    [InlineData(72)][InlineData(76)][InlineData(80)][InlineData(84)][InlineData(88)][InlineData(112)]
    [InlineData(116)][InlineData(120)][InlineData(124)]
    public async Task Rehashed_header_corruption_still_fails_exact_framing(int offset)
    {
        var (a, b) = await Build(await Fixture.Create()); b[offset] ^= 1; var changed = Rehash(a, b); using var input = new TrackedSource(b);
        await Error("INVALID", () => ModelGeometryArtifacts.VerifyAsync(input, changed)); Assert.True(input.CanRead);
    }

    [Theory]
    [InlineData("nan")][InlineData("bounds")][InlineData("index")][InlineData("pair")][InlineData("ordinal")]
    [InlineData("level")][InlineData("sourceAlignment")][InlineData("partition")][InlineData("count")]
    public async Task Rehashed_payload_corruption_cannot_hide_behind_a_new_file_digest(string kind)
    {
        var (a, b) = await Build(await Fixture.Create());
        switch (kind)
        {
            case "nan": Put(b, 128, 0x7fc00000); break;
            case "bounds": PutFloat(b, 128 + 24, -7); break;
            case "index": Put(b, 176, 4); break;
            case "pair": Put(b, 176 + 8, 1); break;
            case "ordinal": Put(b, 224, 1); break;
            case "level": PutWord(b, 224 + 6, 1); break;
            case "sourceAlignment": Put(b, 224 + 16, 1); break;
            case "partition": Put(b, 224 + 32 + 24, 0); break;
            case "count": Put(b, 224 + 20, 1); break;
        }
        using var input = new TrackedSource(b); await Error("INVALID", () => ModelGeometryArtifacts.VerifyAsync(input, Rehash(a, b)));
    }

    [Fact]
    public async Task Same_size_unused_position_mutation_fails_full_hash_and_every_truncation_fails()
    {
        var (a, bytes) = await Build(await Fixture.Create()); var bad = bytes.ToArray(); PutFloat(bad, 128 + 36, 101);
        using var changed = new TrackedSource(bad); await Error("INVALID", () => ModelGeometryArtifacts.VerifyAsync(changed, a));
        for (var n = 0; n < bytes.Length; n++)
        { using var truncated = new TrackedSource(bytes[..n], declaredLength: bytes.Length); await Error("INVALID", () => ModelGeometryArtifacts.VerifyAsync(truncated, a)); }
        using var extra = new TrackedSource(bytes.Concat(new byte[1]).ToArray()); await Error("INVALID", () => ModelGeometryArtifacts.VerifyAsync(extra, a));
        using var hiddenExtra = new TrackedSource(bytes.Concat(new byte[1]).ToArray(), declaredLength: bytes.Length); await Error("INVALID", () => ModelGeometryArtifacts.VerifyAsync(hiddenExtra, a));
        using var changedLength = new TrackedSource(bytes, changeLength: true); await Error("INVALID", () => ModelGeometryArtifacts.VerifyAsync(changedLength, a));
    }

    [Theory]
    [InlineData("scope")][InlineData("policy")][InlineData("evaluation")][InlineData("region")]
    [InlineData("count")][InlineData("bounds")][InlineData("sha")][InlineData("binding")][InlineData("size")]
    public async Task Descriptor_manifest_identity_and_known_policy_are_checked_before_source_io(string kind)
    {
        var (a, bytes) = await Build(await Fixture.Create()); a = kind switch
        {
            "scope" => a with { Manifest = a.Manifest with { Scope = "appearance-ready" } },
            "policy" => a with { Manifest = a.Manifest with { EdgePolicy = "deduplicate" } },
            "evaluation" => a with { Manifest = a.Manifest with { Evaluations = new("ready", "notEvaluated", "notEvaluated", "notEvaluated") } },
            "region" => a with { Manifest = a.Manifest with { Positions = new(132, 48) } },
            "count" => a with { Manifest = a.Manifest with { LineIndexCount = 6 } },
            "bounds" => a with { Manifest = a.Manifest with { Bounds = new(new(-6, -4, -0f), new(3, 7, 8)) } },
            "sha" => a with { Sha256 = new string('7', 64) }, "binding" => a with { BindingSha256 = new string('7', 64) },
            _ => a with { ByteLength = 2097153 }
        };
        using var source = new TrackedSource(bytes); await Error(kind == "size" ? "LIMIT" : "INVALID", () => ModelGeometryArtifacts.VerifyAsync(source, a)); Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task All_degenerate_geometry_keeps_finite_equal_bounds_without_claiming_a_camera_fit()
    {
        var f = await Fixture.Create(indices: [0, 0, 0], sections: [Section(0, 3)]); var (a, bytes) = await Build(f);
        Assert.Equal(a.Manifest.Bounds.Min, a.Manifest.Bounds.Max); Assert.Equal(6, a.Manifest.LineIndexCount);
        using var input = new TrackedSource(bytes); await ModelGeometryArtifacts.VerifyAsync(input, a);
        var empty = await Fixture.Create(sections: [Section(0, 0)]); using var output = new TrackedOutput();
        await Error("UNSUPPORTED", () => ModelGeometryArtifacts.BuildAsync(empty.Skin, empty.Binding, output)); Assert.Equal(0, output.Writes);
    }

    [Fact]
    public async Task High_word_section_start_and_source_lookup_provenance_survive_without_position_slice_guesses()
    {
        var indices = new ushort[65541]; indices[65538] = 0; indices[65539] = 1; indices[65540] = 2;
        var f = await Fixture.Create(indices: indices, sections: [Section(65538, 3, level: 1)]); var (a, bytes) = await Build(f);
        Assert.Equal(1, W(bytes, a.Manifest.Sections.Offset + 6)); Assert.Equal(65538u, U(bytes, a.Manifest.Sections.Offset + 16));
        using var input = new TrackedSource(bytes); await ModelGeometryArtifacts.VerifyAsync(input, a);
    }

    [Fact]
    public async Task Maximum_realizable_envelope_is_exact_and_verifier_io_is_linear_even_for_far_apart_indices()
    {
        var lookup = Enumerable.Range(0, 65536).Select(i => (ushort)i).ToArray(); var indices = new ushort[131070];
        for (var i = 0; i < indices.Length; i++) indices[i] = (ushort)(i % 2 == 0 ? 0 : 65534);
        var sections = Enumerable.Range(0, 256).Select(i => Section(0, i < 2 ? (ushort)65535 : (ushort)0, vertexCount: 65535)).ToArray();
        var f = await Fixture.Create(vertices: 65536, lookup: lookup, indices: indices, sections: sections);
        var timing = System.Diagnostics.Stopwatch.StartNew();
        var assembledCount = M2StaticGeometry.Create(f.Skin.Model, f.Skin).Indices.Count; timing.Stop();
        Assert.Equal(131070, assembledCount);
        testOutput.WriteLine($"Single authored maximum original assembler observation: {timing.Elapsed.TotalMilliseconds:F3} ms; no latency assertion.");
        using var destination = new TrackedOutput(); var a = await ModelGeometryArtifacts.BuildAsync(f.Skin, f.Binding, destination);
        Assert.Equal(1843312, a.ByteLength); Assert.InRange(destination.MaximumRequest, 1, 65536);
        using var source = new TrackedSource(destination.ToArray()); await ModelGeometryArtifacts.VerifyAsync(source, a);
        Assert.Equal(a.ByteLength, source.BytesRead); Assert.Equal(0, source.Rewinds); Assert.InRange(source.MaximumRequest, 1, 65536); Assert.InRange(source.Reads, 1, 40);
        // Exact native-ushort proof matters even where the wider header range would otherwise fit.
        var bad = destination.ToArray(); Put(bad, a.Manifest.Sections.Offset + 12, 65536);
        using var forged = new TrackedSource(bad); await Error("INVALID", () => ModelGeometryArtifacts.VerifyAsync(forged, Rehash(a, bad)));
    }

    [Fact]
    public async Task Overlapping_section_expansion_is_bounded_before_any_provisional_output()
    {
        var f = await Fixture.Create(indices: new ushort[65535], sections: [Section(0, 65535), Section(0, 65535), Section(0, 3)]);
        using var output = new TrackedOutput(); await Error("LIMIT", () => ModelGeometryArtifacts.BuildAsync(f.Skin, f.Binding, output)); Assert.Equal(0, output.Writes);
    }

    [Theory]
    [InlineData("vertices")][InlineData("metadata")]
    public async Task Individually_oversized_typed_directories_refuse_before_first_write(string kind)
    {
        var f = kind switch
        {
            "vertices" => await Fixture.Create(vertices: 65537),
            _ => await Fixture.Create(metadata: 8193)
        };
        using var output = new TrackedOutput(); await Error("LIMIT", () => ModelGeometryArtifacts.BuildAsync(f.Skin, f.Binding, output));
        Assert.Equal(0, output.Writes); Assert.Equal(0, output.Length);
    }

    [Theory]
    [InlineData("vertices")][InlineData("lookup")][InlineData("global")][InlineData("expanded")][InlineData("sections")]
    public async Task Out_of_profile_manifest_counts_fail_before_allocation_or_source_read(string kind)
    {
        var (a, bytes) = await Build(await Fixture.Create()); var m = a.Manifest;
        m = kind switch { "vertices" => m with { VertexCount = 65537 }, "lookup" => m with { VertexLookupCount = 65537 },
            "global" => m with { SourceTriangleIndexCount = 131073 }, "expanded" => m with { SectionIndexCount = 131073 }, _ => m with { SectionCount = 257 } };
        using var source = new TrackedSource(bytes); await Error("LIMIT", () => ModelGeometryArtifacts.VerifyAsync(source, a with { Manifest = m })); Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task Finite_tight_degenerate_and_subnormal_bounds_are_artifact_data_not_a_camera_promise()
    {
        var f = await Fixture.Create(indices: [0, 0, 0], sections: [Section(0, 3)]); var (a, bytes) = await Build(f);
        // Rehashed finite subnormal point, with coherently updated tight bounds, remains valid app geometry.
        var p = new Vector3(float.Epsilon, -float.Epsilon, float.Epsilon); PutFloat(bytes, 128 + 24, p.X); PutFloat(bytes, 128 + 28, p.Y); PutFloat(bytes, 128 + 32, p.Z);
        for (var i = 0; i < 3; i++) { var v = i == 1 ? p.Y : p.X; PutFloat(bytes, 88 + i * 4, v); PutFloat(bytes, 100 + i * 4, v); }
        a = a with { Manifest = a.Manifest with { Bounds = new(p, p) } }; a = Rehash(a, bytes);
        using var source = new TrackedSource(bytes); await ModelGeometryArtifacts.VerifyAsync(source, a);
        // This is a structural verifier exercise, not a claim that altered derivatives retain source authority.
    }

    [Fact]
    public async Task Backpressure_is_awaited_and_cancellation_at_each_read_write_keeps_caller_streams_open()
    {
        var f = await Fixture.Create(); using var held = new TrackedOutput(hold: true); var pending = ModelGeometryArtifacts.BuildAsync(f.Skin, f.Binding, held);
        await held.Started.Task; Assert.False(pending.IsCompleted); Assert.Equal(1, held.Writes); held.Release.TrySetResult(); var a = await pending;
        var bytes = held.ToArray(); using var complete = new TrackedSource(bytes, fragment: 13); await ModelGeometryArtifacts.VerifyAsync(complete, a);
        for (var at = 1; at <= complete.Reads; at++)
        { using var cancel = new CancellationTokenSource(); using var input = new TrackedSource(bytes, fragment: 13, cancel: cancel, cancelAt: at); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ModelGeometryArtifacts.VerifyAsync(input, a, cancel.Token)); Assert.True(input.CanRead); }
        for (var at = 1; at <= held.Writes; at++)
        { using var cancel = new CancellationTokenSource(); using var output = new TrackedOutput(cancel: cancel, cancelAt: at); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ModelGeometryArtifacts.BuildAsync(f.Skin, f.Binding, output, cancel.Token)); Assert.True(output.CanWrite); }
        using var before = new CancellationTokenSource(); before.Cancel(); using var untouched = new TrackedOutput();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ModelGeometryArtifacts.BuildAsync(f.Skin, f.Binding, untouched, before.Token)); Assert.Equal(0, untouched.Writes);
        using var cancelHeld = new CancellationTokenSource(); using var blocked = new TrackedOutput(hold: true); var interrupted = ModelGeometryArtifacts.BuildAsync(f.Skin, f.Binding, blocked, cancelHeld.Token);
        await blocked.Started.Task; cancelHeld.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interrupted); Assert.True(blocked.CanWrite);
    }

    [Fact]
    public async Task Streams_must_start_empty_or_complete_and_remain_caller_owned()
    {
        var f = await Fixture.Create(); using var nonempty = new MemoryStream(); nonempty.WriteByte(1); nonempty.Position = 0;
        await Error("INVALID", () => ModelGeometryArtifacts.BuildAsync(f.Skin, f.Binding, nonempty)); Assert.True(nonempty.CanWrite);
        var (a, bytes) = await Build(f); using var wrongPosition = new TrackedSource(bytes); wrongPosition.Position = 1;
        await Error("INVALID", () => ModelGeometryArtifacts.VerifyAsync(wrongPosition, a)); Assert.Equal(0, wrongPosition.Reads);
    }

    private static async Task<(GeometryArtifact Artifact, byte[] Bytes)> Build(Fixture f)
    { using var output = new MemoryStream(); var a = await ModelGeometryArtifacts.BuildAsync(f.Skin, f.Binding, output); return (a, output.ToArray()); }
    private static async Task Error(string suffix, Func<Task> action) => Assert.Equal("GEOMETRY_ARTIFACT_" + suffix, (await Assert.ThrowsAsync<DiscoveryException>(action)).Code);
    private static GeometryArtifact Rehash(GeometryArtifact a, byte[] bytes)
    {
        var sha = SHA256.HashData(bytes); using var domain = new MemoryStream(); domain.Write("vibeminer-model-geometry-artifact-v1\0"u8);
        domain.Write(Convert.FromHexString(a.BindingSha256)); domain.Write(sha); using var writer = new BinaryWriter(domain, Encoding.ASCII, true); writer.Write((ulong)bytes.Length);
        var id = Convert.ToHexStringLower(SHA256.HashData(domain.ToArray())); var text = Convert.ToHexStringLower(sha);
        return a with { Sha256 = text, ArtifactId = id, Manifest = a.Manifest with { Sha256 = text, ArtifactId = id } };
    }
    private static M2Section Section(uint start, ushort count, ushort level = 0, ushort vertexCount = 3, ushort id = 7) =>
        new(id, level, 0, vertexCount, start, count, 0, 0, 0, 0, Vector3.Zero, Vector3.Zero, 0);
    private static uint U(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
    private static ushort W(byte[] b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
    private static void Put(byte[] b, int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);
    private static void PutWord(byte[] b, int at, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), v);
    private static void PutFloat(byte[] b, int at, float v) => BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(at), v);

    // Entirely authored minimal sources; synthetic source bindings are shape inputs, never actual verification claims.
    private sealed record Fixture(M2GeometrySkin Skin, GeometrySourceBinding Binding)
    {
        internal static async Task<Fixture> Create(int vertices = 4, ushort[]? lookup = null, ushort[]? indices = null, M2Section[]? sections = null, M2Limits? limits = null, int metadata = 0)
        {
            lookup ??= [2, 0, 1]; indices ??= [0, 1, 2, 2, 2, 2]; sections ??= [Section(0, 3), Section(0, 3, id: 9), Section(3, 0, id: 0)];
            var body = new byte[304 + vertices * 48 + metadata * 2]; "MD20"u8.CopyTo(body); Put(body, 4, 274); Put(body, 60, (uint)vertices); Put(body, 64, 304); Put(body, 68, 1);
            for (var i = 0; i < vertices; i++)
            {
                var position = vertices == 4 ? i switch { 0 => new Vector3(-0f, 2, 0), 1 => new Vector3(3, -4, 5), 2 => new Vector3(-6, 7, 8), _ => new Vector3(100) } : new Vector3(i, 0, 0);
                var at = 304 + i * 48; PutFloat(body, at, position.X); PutFloat(body, at + 4, position.Y); PutFloat(body, at + 8, position.Z); PutFloat(body, at + 28, 1);
            }
            if (metadata > 0) { Put(body, 120, (uint)metadata); Put(body, 124, (uint)(304 + vertices * 48)); }
            var modelBytes = new byte[body.Length + 20]; "MD21"u8.CopyTo(modelBytes); Put(modelBytes, 4, (uint)body.Length); body.CopyTo(modelBytes, 8); "SFID"u8.CopyTo(modelBytes.AsSpan(body.Length + 8)); Put(modelBytes, body.Length + 12, 4); Put(modelBytes, body.Length + 16, 900);
            using var modelStream = new MemoryStream(modelBytes); var model = await M2ModelReader.ReadAsync(modelStream, limits);
            var trianglesAt = 56 + lookup.Length * 2; var propertiesAt = trianglesAt + indices.Length * 2; var sectionsAt = propertiesAt + lookup.Length * 4;
            var skinBytes = new byte[sectionsAt + sections.Length * 48]; "SKIN"u8.CopyTo(skinBytes); Put(skinBytes, 4, (uint)lookup.Length); Put(skinBytes, 8, 56);
            Put(skinBytes, 12, (uint)indices.Length); Put(skinBytes, 16, (uint)trianglesAt); Put(skinBytes, 20, (uint)lookup.Length); Put(skinBytes, 24, (uint)propertiesAt);
            Put(skinBytes, 28, (uint)sections.Length); Put(skinBytes, 32, (uint)sectionsAt);
            for (var i = 0; i < lookup.Length; i++) PutWord(skinBytes, 56 + i * 2, lookup[i]);
            for (var i = 0; i < indices.Length; i++) PutWord(skinBytes, trianglesAt + i * 2, indices[i]);
            for (var i = 0; i < sections.Length; i++)
            {
                var s = sections[i]; var at = sectionsAt + i * 48; PutWord(skinBytes, at, s.Id); PutWord(skinBytes, at + 2, s.Level);
                PutWord(skinBytes, at + 4, s.VertexStart); PutWord(skinBytes, at + 6, s.VertexCount); PutWord(skinBytes, at + 8, (ushort)s.TriangleStart); PutWord(skinBytes, at + 10, s.TriangleCount);
            }
            using var skinStream = new MemoryStream(skinBytes); var skin = await M2GeometrySkinReader.ReadAsync(skinStream, model, limits);
            return new(skin, new(new(new string('1', 32), new string('2', 32), new string('3', 64), 100, modelBytes.Length, "content-blte-v1"),
                new(new string('4', 32), new string('5', 32), new string('6', 64), 80, skinBytes.Length, "content-blte-v1"), 800, 900, 0));
        }
    }
    private sealed class TrackedSource(byte[] bytes, int fragment = 65536, long? declaredLength = null, bool changeLength = false,
        CancellationTokenSource? cancel = null, int cancelAt = int.MaxValue) : Stream
    {
        private readonly MemoryStream inner = new(bytes); public int Reads, MaximumRequest, Rewinds; public long BytesRead;
        public override bool CanRead => inner.CanRead; public override bool CanSeek => true; public override bool CanWrite => false;
        public override long Length => (declaredLength ?? inner.Length) + (changeLength && Reads > 0 ? 1 : 0);
        public override long Position { get => inner.Position; set { if (value < inner.Position) Rewinds++; inner.Position = value; } }
        public override async ValueTask<int> ReadAsync(Memory<byte> target, CancellationToken token = default)
        { if (++Reads == cancelAt) cancel?.Cancel(); token.ThrowIfCancellationRequested(); MaximumRequest = Math.Max(MaximumRequest, target.Length); var n = await inner.ReadAsync(target[..Math.Min(fragment, target.Length)], token); BytesRead += n; return n; }
        public override int Read(byte[] b, int o, int c) => throw new InvalidOperationException("Synchronous read forbidden");
        public override long Seek(long offset, SeekOrigin origin) => throw new InvalidOperationException("Verifier must not seek");
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Flush() => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
    private sealed class TrackedOutput(bool hold = false, CancellationTokenSource? cancel = null, int cancelAt = int.MaxValue) : MemoryStream
    {
        public int Writes, MaximumRequest;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        { Writes++; Started.TrySetResult(); if (Writes == cancelAt) cancel?.Cancel(); token.ThrowIfCancellationRequested(); if (hold && Writes == 1) await Release.Task.WaitAsync(token); MaximumRequest = Math.Max(MaximumRequest, buffer.Length); await base.WriteAsync(buffer, token); }
    }
}
