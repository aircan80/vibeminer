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
