using System.Security.Cryptography;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;
using static VibeMiner.Backend.Tests.AppearanceModels;

namespace VibeMiner.Backend.Tests;

// Real shared acquisition, BLTE, M2 and BLP pipeline over authored bytes; no game data or network.
[Collection("Creature projection builder")]
public sealed class CreatureGeometryAppearanceTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);
    private static Task Progress(CreatureGeometryProgress _) => Task.CompletedTask;
    private static CreatureGeometryStore Store(CreatureGeometrySourcesFixture f) => new(f.Catalog, f.Projection.Objects, f.Displays, f.Sessions, f.Shared, f.Resources);
    private static CreatureGeometrySourcesFixture.Textured Variation(int variation = (int)CreatureGeometrySourcesFixture.TextureId, int extended = 0,
        bool encrypted = false, byte[]? blp = null) => new(Simple(), [new()], variation, extended, encrypted, blp);
    private static bool Requested(CreatureGeometrySourcesFixture f) => f.Http.Requests.Any(r => f.Http.Responses.TryGetValue(r, out var b) && ReferenceEquals(b, f.TextureEncoded));

    [Fact]
    public async Task A_ready_chain_publishes_a_verified_display_keyed_VMA1_that_read_open_and_reuse_keep_offline()
    {
        await using var f = await CreatureGeometrySourcesFixture.CreateAsync(textured: Variation()); await using var store = Store(f);
        await using var service = new CreatureGeometryService(store);
        var ready = await service.PrepareAsync(new(f.Request)).WaitAsync(Guard);
        Assert.Equal(CreatureGeometryState.Ready, ready.State);
        var appearance = ready.Appearance!; Assert.Equal(CreatureGeometryAppearanceState.Ready, appearance.State);
        var texture = Assert.Single(appearance.Textures);
        Assert.Equal((0, 11, CreatureGeometryTextureBinding.Variation, 0, "779", CreatureGeometryTextureState.Decoded),
            (texture.Ordinal, texture.Type, texture.Binding, texture.Variation, texture.FileDataId, texture.State));
        Assert.Equal(new CreatureGeometryTextureImage(8, 8, 0, 8, 8, "bgra8"), texture.Image);
        Assert.Equal(["779", "0", "0", "0"], appearance.Display!.TextureVariations); Assert.Equal("0", appearance.Display.ExtendedDisplayInfoId);
        var manifest = appearance.Manifest!; Assert.Equal((3, 3, 1, 1, 256), (manifest.VertexCount, manifest.IndexCount, manifest.DrawCount, manifest.TextureCount, manifest.Texels.ByteLength));
        Assert.Equal(123u, manifest.DisplayId); Assert.Equal(ready.Manifest!.BindingSha256, manifest.GeometryBindingSha256);
        Assert.Equal(0, store.Pressure.Payload);

        var requests = f.Http.Requests.Count; f.Http.Offline = true;
        var read = await service.ReadAsync(new(f.Request, ready.Manifest.ArtifactId));
        Assert.Equal(JsonSerializer.Serialize(ready, CreaturesPolicy.Json), JsonSerializer.Serialize(read, CreaturesPolicy.Json));
        var lease = await service.OpenAsync(new(f.Request, manifest.ArtifactId));
        Assert.Equal(manifest.ArtifactId, lease.Appearance!.ArtifactId); Assert.Equal(ready.Manifest.ArtifactId, lease.Manifest.ArtifactId); var bytes = new byte[manifest.ByteLength]; await lease.Content.ReadExactlyAsync(bytes);
        await lease.DisposeAsync(); await lease.Retirement;
        Assert.Equal("VMA1"u8.ToArray(), bytes[..4]); Assert.Equal(manifest.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        Assert.True(bytes[^256..].All(b => b == 0x40));
        var reused = await service.PrepareAsync(new(f.Request)).WaitAsync(Guard);
        Assert.Equal(JsonSerializer.Serialize(ready.Appearance, CreaturesPolicy.Json), JsonSerializer.Serialize(reused.Appearance, CreaturesPolicy.Json));
        Assert.Equal(requests, f.Http.Requests.Count); Assert.Equal(0, store.Pressure.Payload);
    }

    [Fact]
    public async Task Same_size_VMA1_corruption_reads_as_corrupt_and_keeps_the_lines()
    {
        await using var f = await CreatureGeometrySourcesFixture.CreateAsync(textured: Variation()); await using var store = Store(f);
        var ready = await store.PrepareAsync(new(f.Request), Progress, default).WaitAsync(Guard); var sha = ready.Appearance!.Manifest!.Sha256; var id = ready.Appearance.Manifest.ArtifactId;
        var path = Path.Combine(f.Projection.DirectoryPath, "objects-v1", sha[..2], sha[2..]);
        var bytes = await File.ReadAllBytesAsync(path); bytes[^1] ^= 1; File.SetAttributes(path, FileAttributes.Normal); await File.WriteAllBytesAsync(path, bytes);
        var read = await store.ReadAsync(new(f.Request), default);
        Assert.Equal(CreatureGeometryState.Ready, read.State); Assert.NotNull(read.Manifest);
        Assert.Equal(CreatureGeometryAppearanceState.Corrupt, read.Appearance!.State); Assert.Null(read.Appearance.Manifest);
        await Assert.ThrowsAnyAsync<DiscoveryException>(() => store.OpenAsync(new(f.Request, id), default));
    }

    [Theory]
    [InlineData("character")][InlineData("zero")][InlineData("negative")]
    public async Task Display_refusals_stop_before_any_texture_demand_and_are_retained(string kind)
    {
        var textured = kind switch { "character" => Variation(extended: 5), "zero" => Variation(variation: 0), _ => Variation(variation: -3) };
        await using var f = await CreatureGeometrySourcesFixture.CreateAsync(textured: textured); await using var store = Store(f);
        var result = await store.PrepareAsync(new(f.Request), Progress, default).WaitAsync(Guard);
        Assert.Equal(CreatureGeometryState.Ready, result.State); Assert.NotNull(result.Manifest); Assert.False(Requested(f));
        var appearance = result.Appearance!;
        if (kind == "character") { Assert.Equal(CreatureGeometryAppearanceState.CharacterModel, appearance.State); Assert.Equal("5", appearance.Display!.ExtendedDisplayInfoId); }
        else
        {
            Assert.Equal(CreatureGeometryAppearanceState.TextureUnresolved, appearance.State); Assert.Equal(0, appearance.Problem!.TextureOrdinal);
            var chain = Assert.Single(appearance.Textures); Assert.Null(chain.FileDataId); Assert.Equal(CreatureGeometryTextureState.Unresolved, chain.State);
        }
        var read = await store.ReadAsync(new(f.Request), default);
        Assert.Equal(JsonSerializer.Serialize(appearance, CreaturesPolicy.Json), JsonSerializer.Serialize(read.Appearance, CreaturesPolicy.Json));
    }

    [Fact]
    public async Task A_batch_refusal_names_its_batch_and_keeps_geometry()
    {
        var spec = Simple() with { Materials = [(0, 4)] };
        await using var f = await CreatureGeometrySourcesFixture.CreateAsync(textured: new(spec, [new()])); await using var store = Store(f);
        var result = await store.PrepareAsync(new(f.Request), Progress, default).WaitAsync(Guard);
        Assert.Equal(CreatureGeometryState.Ready, result.State);
        Assert.Equal(CreatureGeometryAppearanceState.BlendUnsupported, result.Appearance!.State); Assert.Equal(0, result.Appearance.Problem!.BatchOrdinal);
        Assert.False(Requested(f));
    }

    [Fact]
    public async Task A_two_stage_env_combiner_binds_both_stages_writes_normals_and_counts_blended_layers()
    {
        // Stage 0: display variation 779 (no wrap). Stage 1: TXID 779 wrapped in U, so a second slot. Section 1 is an additive layer.
        var spec = new Model([new(11, 0), new(0, 1, CreatureGeometrySourcesFixture.TextureId)], [(0, 0), (0x15, 4)], [0, 1], [0], [ushort.MaxValue, ushort.MaxValue],
            [[[32767]]], [0x20]);
        await using var f = await CreatureGeometrySourcesFixture.CreateAsync(textured: new(spec,
            [new(Section: 0, Shader: 0x8002, Flags: 0x80, TextureCount: 2), new(Section: 1, Material: 1, Shader: 0x4014, Flags: 0x80, TextureCount: 2)], Sections: 2));
        await using var store = Store(f);
        var result = await store.PrepareAsync(new(f.Request), Progress, default).WaitAsync(Guard); var appearance = result.Appearance!;
        Assert.True(appearance.State == CreatureGeometryAppearanceState.Ready, appearance.Problem?.Code + " " + appearance.Problem?.Message);
        Assert.Equal([(0, CreatureGeometryTextureBinding.Variation, "779"), (1, CreatureGeometryTextureBinding.Txid, "779")],
            appearance.Textures.Select(t => (t.Ordinal, t.Binding, t.FileDataId)));
        var manifest = appearance.Manifest!;
        Assert.Equal((1, 2, 3 * 12, 48), (manifest.DrawCount, manifest.TextureCount, manifest.Normals.ByteLength, manifest.Draws.ByteLength));
        Assert.Equal((1, 0), (appearance.Effects!.BlendedBatches, appearance.Effects.EnvTransforms));
        var lease = await store.OpenAsync(new(f.Request, manifest.ArtifactId), default); var bytes = new byte[manifest.ByteLength]; await lease.Content.ReadExactlyAsync(bytes);
        await lease.DisposeAsync(); await lease.Retirement;
        var r = manifest.Draws.Offset; uint U(int at) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
        Assert.Equal((4u, 0u, 0u, 1u, 1u), (U(r + 20), U(r + 24), U(r + 28), U(r + 32), U(r + 36))); Assert.Equal((0, 2), (bytes[r + 40], bytes[r + 41]));
        var read = await store.ReadAsync(new(f.Request), default);
        Assert.Equal(JsonSerializer.Serialize(appearance, CreaturesPolicy.Json), JsonSerializer.Serialize(read.Appearance, CreaturesPolicy.Json));
    }

    // D095 with #69's combiners: both stages of a two-stage batch use the standard variant and record it.
    [Fact]
    public async Task Both_stages_of_a_two_stage_batch_draw_the_standard_variant_of_a_HighResTexture_split()
    {
        var spec = new Model([new(11, 0), new(0, 1, CreatureGeometrySourcesFixture.TextureId)], [(0, 0), (0x15, 4)], [0, 1], [0], [ushort.MaxValue, ushort.MaxValue],
            [[[32767]]], [0x20]);
        await using var f = await CreatureGeometrySourcesFixture.CreateAsync(textured: new(spec,
            [new(Section: 0, Shader: 0x8002, Flags: 0x80, TextureCount: 2), new(Section: 1, Material: 1, Shader: 0x4014, Flags: 0x80, TextureCount: 2)], Sections: 2,
            Variants: [(2, 0x1, true), (2, 0x80, false), (2, 0x81, true)]));
        await using var store = Store(f);
        var appearance = (await store.PrepareAsync(new(f.Request), Progress, default).WaitAsync(Guard)).Appearance!;
        Assert.True(appearance.State == CreatureGeometryAppearanceState.Ready, appearance.Problem?.Code + " " + appearance.Problem?.Message);
        Assert.Equal(2, appearance.Textures.Count);
        Assert.All(appearance.Textures, t => Assert.Equal((CreatureGeometryValidation.TextureVariantPolicy, t.Source!.ContentKey, 1u), (t.Variant!.Policy, t.Variant.ContentKey, t.Variant.HighResolutionContentFlags)));
        Assert.DoesNotContain(f.Http.Requests, r => f.Http.Responses.TryGetValue(r, out var b) && ReferenceEquals(b, f.HighTextureEncoded));
    }

    [Fact]
    public async Task An_unsupported_combiner_is_retained_by_name_without_texture_demand()
    {
        var spec = new Model([new(11, 0), new(0, 0, CreatureGeometrySourcesFixture.TextureId)], [(0, 0)], [0, 1], [0], [ushort.MaxValue, ushort.MaxValue], [[[32767]]], [0x20]);
        await using var f = await CreatureGeometrySourcesFixture.CreateAsync(textured: new(spec, [new(Shader: 0x4011, Flags: 0x80, TextureCount: 2)])); await using var store = Store(f);
        var result = await store.PrepareAsync(new(f.Request), Progress, default).WaitAsync(Guard); var appearance = result.Appearance!;
        Assert.Equal(CreatureGeometryAppearanceState.CombinerUnsupported, appearance.State); Assert.Equal("CREATURE_APPEARANCE_COMBINER_UNSUPPORTED", appearance.Problem!.Code);
        Assert.Contains("Combiners_Mod_Mod with Diffuse_T1_T2 (shader 0x4011", appearance.Problem.Message); Assert.False(Requested(f));
        Assert.Equal(JsonSerializer.Serialize(appearance, CreaturesPolicy.Json), JsonSerializer.Serialize((await store.ReadAsync(new(f.Request), default)).Appearance, CreaturesPolicy.Json));
    }

    [Fact]
    public async Task An_encrypted_texture_names_its_ordinal_and_is_never_retained()
    {
        await using var f = await CreatureGeometrySourcesFixture.CreateAsync(textured: Variation(encrypted: true)); await using var store = Store(f);
        var result = await store.PrepareAsync(new(f.Request), Progress, default).WaitAsync(Guard);
        Assert.Equal(CreatureGeometryState.Ready, result.State);
        Assert.Equal(CreatureGeometryAppearanceState.TextureProtected, result.Appearance!.State); Assert.Equal(0, result.Appearance.Problem!.TextureOrdinal);
        Assert.Equal(CreatureGeometryTextureState.Protected, Assert.Single(result.Appearance.Textures).State);
        Assert.Equal(CreatureGeometryAppearanceState.NotPrepared, (await store.ReadAsync(new(f.Request), default)).Appearance!.State);
        Assert.Equal(0, store.Pressure.Payload);
    }

    [Theory]
    [InlineData(2048, 3, 1, 1024)]
    [InlineData(2048, 1, -1, 0)]
    public async Task The_largest_declared_mip_within_1024_is_decoded_and_none_is_invented(int width, int levels, int mip, int chosen)
    {
        await using var f = await CreatureGeometrySourcesFixture.CreateAsync(textured: Variation(blp: Blp(width, 4, levels))); await using var store = Store(f);
        var result = await store.PrepareAsync(new(f.Request), Progress, default).WaitAsync(Guard); var appearance = result.Appearance!;
        if (mip < 0) { Assert.Equal(CreatureGeometryAppearanceState.ResourceLimit, appearance.State); Assert.Equal("BLP_MIP_UNAVAILABLE", appearance.Problem!.Code); return; }
        var image = Assert.Single(appearance.Textures).Image!;
        Assert.Equal((mip, chosen, 2, width, 4), (image.MipLevel, image.Width, image.Height, image.BaseWidth, image.BaseHeight));
    }

    // D095 (#70): Classic texture memberships split only by HighResTexture 0x1, with LowViolence 0x80 copies of both.
    private static readonly (uint, uint, bool)[] HighResSplit = [(2, 0x1, true), (2, 0x80, false), (2, 0x81, true)];

    [Fact]
    public async Task A_HighResTexture_split_draws_the_standard_variant_names_it_in_the_chain_and_never_fetches_the_high_one()
    {
        await using var f = await CreatureGeometrySourcesFixture.CreateAsync(textured: new(Simple(), [new()], Variants: HighResSplit)); await using var store = Store(f);
        var result = await store.PrepareAsync(new(f.Request), Progress, default).WaitAsync(Guard); var appearance = result.Appearance!;
        Assert.True(appearance.State == CreatureGeometryAppearanceState.Ready, appearance.Problem?.Code + " " + appearance.Problem?.Message);
        var texture = Assert.Single(appearance.Textures);
        Assert.Equal(new CreatureGeometryTextureImage(8, 8, 0, 8, 8, "bgra8"), texture.Image);
        var variant = texture.Variant!;
        Assert.Equal(("root-texture-standard-resolution-v1", texture.Source!.ContentKey, 0u, 1u), (variant.Policy, variant.ContentKey, variant.ContentFlags, variant.HighResolutionContentFlags));
        Assert.NotEqual(variant.ContentKey, variant.HighResolutionContentKey);
        Assert.True(Requested(f)); Assert.DoesNotContain(f.Http.Requests, r => f.Http.Responses.TryGetValue(r, out var b) && ReferenceEquals(b, f.HighTextureEncoded));
        var read = await store.ReadAsync(new(f.Request), default);
        Assert.Equal(JsonSerializer.Serialize(appearance, CreaturesPolicy.Json), JsonSerializer.Serialize(read.Appearance, CreaturesPolicy.Json));
        Assert.Equal(0, store.Pressure.Payload);
    }

    [Theory]
    [InlineData("locale")][InlineData("crossed copy")][InlineData("other bit")]
    public async Task Any_other_texture_variant_disagreement_stays_ambiguous_and_fetches_nothing(string kind)
    {
        (uint, uint, bool)[] variants = kind switch
        { "locale" => [(4, 0x1, true)], "crossed copy" => [(2, 0x1, true), (2, 0x80, true)], _ => [(2, 0x9, true)] };
        await using var f = await CreatureGeometrySourcesFixture.CreateAsync(textured: new(Simple(), [new()], Variants: variants)); await using var store = Store(f);
        var result = await store.PrepareAsync(new(f.Request), Progress, default).WaitAsync(Guard); var appearance = result.Appearance!;
        Assert.Equal(CreatureGeometryState.Ready, result.State);
        Assert.Equal(CreatureGeometryAppearanceState.TextureAmbiguous, appearance.State); Assert.Equal("CREATURE_APPEARANCE_TEXTURE_AMBIGUOUS", appearance.Problem!.Code);
        var texture = Assert.Single(appearance.Textures); Assert.Equal(CreatureGeometryTextureState.Ambiguous, texture.State); Assert.Null(texture.Variant);
        Assert.False(Requested(f));
    }

    // One FileDataID drawn through three wrap combinations is three 1,024² slots: 12 MiB, over the 8 MiB bound.
    private static CreatureGeometrySourcesFixture.Textured ThreeSlots(byte[] blp) => new(
        new([new(0, 0, CreatureGeometrySourcesFixture.TextureId), new(0, 1, CreatureGeometrySourcesFixture.TextureId), new(0, 2, CreatureGeometrySourcesFixture.TextureId)],
            [(0, 0)], [0, 1, 2], [0], [ushort.MaxValue], [[[32767]]], [0x20]),
        [new(Section: 0, Combo: 0), new(Section: 1, Combo: 1), new(Section: 2, Combo: 2)], Blp: blp, Sections: 3);

    [Fact]
    public async Task Over_budget_textures_step_down_a_declared_mip_and_stay_within_the_reservation()
    {
        await using var f = await CreatureGeometrySourcesFixture.CreateAsync(textured: ThreeSlots(Blp(1024, 1024, 2))); await using var store = Store(f);
        var result = await store.PrepareAsync(new(f.Request), Progress, default).WaitAsync(Guard); var appearance = result.Appearance!;
        Assert.True(appearance.State == CreatureGeometryAppearanceState.Ready, appearance.Problem?.Code + " " + appearance.Problem?.Message);
        Assert.All(appearance.Textures, t => Assert.Equal(new CreatureGeometryTextureImage(512, 512, 1, 1024, 1024, "bgra8"), t.Image));
        Assert.Equal((3, 3 * 512 * 512 * 4), (appearance.Manifest!.TextureCount, appearance.Manifest.Texels.ByteLength));
        Assert.Equal(0, store.Pressure.Payload);
    }

    [Fact]
    public async Task Textures_over_budget_at_their_smallest_declared_mips_refuse_before_any_pixel_is_held()
    {
        await using var f = await CreatureGeometrySourcesFixture.CreateAsync(textured: ThreeSlots(Blp(1024, 1024, 1))); await using var store = Store(f);
        var result = await store.PrepareAsync(new(f.Request), Progress, default).WaitAsync(Guard);
        Assert.Equal(CreatureGeometryState.Ready, result.State);
        Assert.Equal(CreatureGeometryAppearanceState.ResourceLimit, result.Appearance!.State); Assert.Equal("CREATURE_APPEARANCE_TEXEL_LIMIT", result.Appearance.Problem!.Code);
        Assert.All(result.Appearance.Textures, t => Assert.Equal(CreatureGeometryTextureState.NotReached, t.State));
        Assert.Equal(0, store.Pressure.Payload);
    }

    [Fact]
    public async Task A_retained_v1_appearance_row_reads_not_prepared_and_prepares_again()
    {
        // Every revision is part of the appearance key, so a row written by VMA1 v1 (#63) sits under another key with v1 revisions.
        // Stand one in: the current row, re-keyed and rewritten to the v1 revisions. It is never adopted, read or deleted.
        await using var f = await CreatureGeometrySourcesFixture.CreateAsync(textured: Variation()); await using var store = Store(f);
        var first = await store.PrepareAsync(new(f.Request), Progress, default).WaitAsync(Guard);
        Assert.Equal(CreatureGeometryAppearanceState.Ready, first.Appearance!.State);
        var database = Path.Combine(f.Projection.DirectoryPath, "discovery-v1.sqlite3"); var oldKey = new string('e', 64);
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database};Pooling=False"))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "UPDATE geometry_appearances SET appearance_key=$k, descriptor=replace(replace(descriptor,'model-appearance-vma1-v2','model-appearance-vma1-v1'),"
                + "'m2-one-or-two-stage-combiners-v1','m2-single-texture-opaque-alphakey-v1')";
            command.Parameters.AddWithValue("$k", oldKey); Assert.Equal(1, command.ExecuteNonQuery());
        }
        var read = await store.ReadAsync(new(f.Request), default);
        Assert.Equal(CreatureGeometryState.Ready, read.State); Assert.NotNull(read.Manifest);
        Assert.Equal(CreatureGeometryAppearanceState.NotPrepared, read.Appearance!.State); Assert.Empty(read.Appearance.Textures);
        var again = await store.PrepareAsync(new(f.Request), Progress, default).WaitAsync(Guard);
        Assert.Equal(CreatureGeometryAppearanceState.Ready, again.Appearance!.State);
        Assert.Equal((2, "model-appearance-vma1-v2"), (again.Appearance.Manifest!.FormatVersion, again.Appearance.Manifest.ArtifactRevision));
        Assert.Equal(first.Appearance.Manifest!.ArtifactId, again.Appearance.Manifest.ArtifactId);
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database};Pooling=False"))
        {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT appearance_key FROM geometry_appearances ORDER BY appearance_key";
            using var reader = command.ExecuteReader(); var keys = new List<string>(); while (reader.Read()) keys.Add(reader.GetString(0));
            Assert.Equal(2, keys.Count); Assert.Contains(oldKey, keys);
        }
        Assert.Equal(CreatureGeometryAppearanceState.Ready, (await store.ReadAsync(new(f.Request), default)).Appearance!.State);
    }

    [Fact]
    public async Task A_retained_v18_geometry_upgrades_and_reads_ready_with_appearance_not_prepared()
    {
        await using var f = await CreatureGeometrySourcesFixture.CreateAsync(textured: Variation()); await using var store = Store(f);
        Assert.Equal(CreatureGeometryAppearanceState.Ready, (await store.PrepareAsync(new(f.Request), Progress, default).WaitAsync(Guard)).Appearance!.State);
        var database = Path.Combine(f.Projection.DirectoryPath, "discovery-v1.sqlite3");
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database};Pooling=False"))
        { db.Open(); using var command = db.CreateCommand(); command.CommandText = CatalogMigrationFixture.RemoveV19 + "PRAGMA user_version=18;"; command.ExecuteNonQuery(); }
        await using (await DiscoveryCatalog.OpenAsync(f.Projection.DirectoryPath)) { }
        var read = await store.ReadAsync(new(f.Request), default);
        Assert.Equal(CreatureGeometryState.Ready, read.State); Assert.NotNull(read.Manifest);
        Assert.Equal(CreatureGeometryAppearanceState.NotPrepared, read.Appearance!.State); Assert.Empty(read.Appearance.Textures);
    }
}
