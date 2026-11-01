using System.Buffers.Binary;
using System.Text;
using VibeMiner.Builds;
using VibeMiner.Files;
using VibeMiner.Files.Testing;
using VibeMiner.Formats.Models;
using Xunit;
using static VibeMiner.Formats.Tests.Models.ModelFixtures;

namespace VibeMiner.Formats.Tests.Models;

// Models and skins read through layer 4, over the in-memory file system fake.
public sealed class BuildModelsTests
{
    private static readonly BuildRef Build = new("wow", new string('a', 32), new string('b', 32));
    private const uint ModelId = 100;
    private const string Key = "000000000000007B";

    // SFID 900 (main), 901 and 902 (LOD); TXID 700 on the type-0 texture and 0 on the type-11 one; every other kind too.
    private static byte[] Full(uint[]? txid = null, uint skid = 600, uint[]? sfid = null) => Chunks(
        ("SFID", Words32(sfid ?? [900, 901, 902])), ("TXID", Words32(txid ?? [700, 0])), ("SKID", Words32(skid)),
        ("BFID", Words32(610, 0)), ("AFID", Afid((10, 1, 620), (11, 0, 0))),
        ("PFID", Words32(630)), ("RPID", Words32(640, 0)), ("GPID", Words32(650)), ("MD21", Body()));

    private static byte[] Afid(params (ushort Animation, ushort Variation, uint Id)[] rows)
    {
        var bytes = new byte[rows.Length * 8];
        for (var i = 0; i < rows.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 8), rows[i].Animation);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 8 + 2), rows[i].Variation);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 8 + 4), rows[i].Id);
        }
        return bytes;
    }

    private static FakeFile Stored(uint id, byte[] content, string? path = null) => new(id, path, Content: content, Storage: FileStorage.Stored);

    private static async Task<IBuildFiles> Open(params FakeFile[] files) => await Open(new InMemoryFileSystem(), files);

    private static async Task<IBuildFiles> Open(InMemoryFileSystem system, params FakeFile[] files) =>
        (await system.Add(Build, ["enUS", "deDE"], files).ForAsync(Build, "enUS", default)).Files!;

    private static ModelReference Ref(ModelRead read, ModelReferenceKind kind, int ordinal = 0) =>
        Assert.Single(read.References, r => r.Kind == kind && r.Ordinal == ordinal);

    [Fact]
    public async Task A_chunked_model_reads_and_resolves_every_reference_kind_by_file_id()
    {
        await using var files = await Open(Stored(ModelId, Full(), "creature/a/a.m2"), new FakeFile(900, Content: Skin()),
            new FakeFile(901, NotInEncoding: true), new FakeFile(902, Locales: ["deDE"]), new FakeFile(700), new FakeFile(600),
            new FakeFile(610), new FakeFile(620), new FakeFile(630), new FakeFile(640));
        var read = await new BuildModels(files).ReadModelAsync(FileRef.ForPath("Creature\\A\\A.m2"));

        Assert.Equal(ModelReadState.Read, read.State); Assert.Equal(ModelId, read.FileDataId); Assert.Equal(ModelId, read.File!.FileDataId);
        Assert.NotNull(read.Model); Assert.Null(read.Code); Assert.Empty(read.KeyNames);
        Assert.Equal(1, read.MainSkinCount); Assert.Equal(2, read.LodSkinCount);

        var skin = Ref(read, ModelReferenceKind.Skin);
        Assert.Equal((900u, ModelReferenceResolution.FileDataId, ModelReferenceState.Found), (skin.FileDataId!.Value, skin.Resolution, skin.State));
        Assert.Equal(900u, skin.File!.FileDataId);
        Assert.Equal(ModelReferenceState.NotInEncoding, Ref(read, ModelReferenceKind.LodSkin, 0).State);
        Assert.Equal(FileStorage.NoEncodedCopy, Ref(read, ModelReferenceKind.LodSkin, 0).File!.Storage);
        Assert.Equal(ModelReferenceState.NotInLocale, Ref(read, ModelReferenceKind.LodSkin, 1).State);
        Assert.Null(Ref(read, ModelReferenceKind.LodSkin, 1).File);

        var texture = Ref(read, ModelReferenceKind.Texture, 0);
        Assert.Equal((700u, 0u, ModelReferenceState.Found), (texture.FileDataId!.Value, texture.TextureType!.Value, texture.State));
        var elsewhere = Ref(read, ModelReferenceKind.Texture, 1);
        Assert.Equal((0u, 11u, ModelReferenceState.ResolvedElsewhere, ModelReferenceResolution.None),
            (elsewhere.FileDataId!.Value, elsewhere.TextureType!.Value, elsewhere.State, elsewhere.Resolution));

        Assert.Equal(ModelReferenceState.Found, Ref(read, ModelReferenceKind.Skeleton).State);
        Assert.Equal(ModelReferenceState.Found, Ref(read, ModelReferenceKind.BoneFile, 0).State);
        Assert.Equal(ModelReferenceState.NoExternalFile, Ref(read, ModelReferenceKind.BoneFile, 1).State);
        var animation = Ref(read, ModelReferenceKind.AnimationFile, 0);
        Assert.Equal((ModelReferenceState.Found, (ushort)10, (ushort)1), (animation.State, animation.AnimationId!.Value, animation.Variation!.Value));
        var noAnimation = Ref(read, ModelReferenceKind.AnimationFile, 1);
        Assert.Equal((ModelReferenceState.NoExternalFile, (ushort)11, (ushort)0), (noAnimation.State, noAnimation.AnimationId!.Value, noAnimation.Variation!.Value));
        Assert.Equal((630u, ModelReferenceState.Found), (Ref(read, ModelReferenceKind.PhysicsFile).FileDataId!.Value, Ref(read, ModelReferenceKind.PhysicsFile).State));
        Assert.Equal(ModelReferenceState.Found, Ref(read, ModelReferenceKind.RecursiveParticleModel, 0).State);
        Assert.Equal(ModelReferenceState.NoExternalFile, Ref(read, ModelReferenceKind.RecursiveParticleModel, 1).State);
        // GPID 650 isn't in the build.
        var unknown = Ref(read, ModelReferenceKind.GeometryParticleModel);
        Assert.Equal((650u, ModelReferenceState.UnknownFile, ModelReferenceResolution.FileDataId), (unknown.FileDataId!.Value, unknown.State, unknown.Resolution));
        Assert.Null(unknown.File);
        Assert.Equal(14, read.References.Count);
    }

    [Fact]
    public async Task A_zero_id_is_never_looked_up_and_a_type0_texture_without_one_is_flagged()
    {
        var stats = new List<FileRef>();
        await using var inner = await Open(Stored(ModelId, Full(txid: [0, 0], skid: 0, sfid: [0])));
        await using var files = new ScriptedFiles(inner, (file, _) => { lock (stats) stats.Add(file); return Task.CompletedTask; });
        var read = await new BuildModels(files).ReadModelAsync(ModelId);

        Assert.Equal(ModelReadState.Read, read.State);
        Assert.Equal(ModelReferenceState.NoFileDeclared, Ref(read, ModelReferenceKind.Skin).State);
        Assert.Equal(ModelReferenceState.NoFileDeclared, Ref(read, ModelReferenceKind.Texture, 0).State);
        Assert.Equal(ModelReferenceState.ResolvedElsewhere, Ref(read, ModelReferenceKind.Texture, 1).State);
        Assert.Equal(ModelReferenceState.NoExternalFile, Ref(read, ModelReferenceKind.Skeleton).State);
        Assert.All(read.References.Where(r => r.FileDataId == 0), r => Assert.Equal(ModelReferenceResolution.None, r.Resolution));
        Assert.DoesNotContain(stats, file => file.FileDataId == 0);
        Assert.Equal([ModelId, 610u, 620u, 630u, 640u, 650u], stats.Select(file => file.FileDataId!.Value).Order());
        Assert.Equal(0, read.LodSkinCount);
    }

    [Fact]
    public async Task A_stat_that_throws_is_check_failed_for_that_reference_only()
    {
        await using var inner = await Open(Stored(ModelId, Full()), new FakeFile(700));
        await using var files = new ScriptedFiles(inner, (file, _) =>
            file.FileDataId == 610 ? throw new IOException("The object index is unreadable.") : Task.CompletedTask);
        var read = await new BuildModels(files).ReadModelAsync(ModelId);

        Assert.Equal(ModelReadState.Read, read.State);
        var failed = Ref(read, ModelReferenceKind.BoneFile, 0);
        Assert.Equal((ModelReferenceState.CheckFailed, "The object index is unreadable.", ModelReferenceResolution.FileDataId), (failed.State, failed.Message, failed.Resolution));
        Assert.Equal(ModelReferenceState.Found, Ref(read, ModelReferenceKind.Texture, 0).State);
    }

    [Theory]
    [InlineData("PFID", 0)][InlineData("PFID", 8)][InlineData("PFID", 3)][InlineData("RPID", 6)][InlineData("GPID", 2)]
    public async Task A_PFID_RPID_or_GPID_of_a_bad_length_is_rejected(string id, int length)
    {
        var bytes = Chunks(("SFID", Words32(900)), ("TXID", Words32(700, 0)), (id, new byte[length]), ("MD21", Body()));
        await using var files = await Open(Stored(ModelId, bytes));
        var read = await new BuildModels(files).ReadModelAsync(ModelId);
        Assert.Equal((ModelReadState.Rejected, "MODEL_INVALID"), (read.State, read.Code));
        Assert.Null(read.Model); Assert.Empty(read.References);
    }

    [Fact]
    public async Task Empty_RPID_and_GPID_declare_nothing()
    {
        var bytes = Chunks(("SFID", Words32(900)), ("TXID", Words32(700, 0)), ("RPID", []), ("GPID", []), ("MD21", Body()));
        await using var files = await Open(Stored(ModelId, bytes));
        var read = await new BuildModels(files).ReadModelAsync(ModelId);
        Assert.Equal(ModelReadState.Read, read.State);
        Assert.DoesNotContain(read.References, r => r.Kind is ModelReferenceKind.RecursiveParticleModel or ModelReferenceKind.GeometryParticleModel or ModelReferenceKind.PhysicsFile);
    }

    // A 264 MD20 with both textures type 0 and named.
    private static byte[] Legacy(string first, string second)
    {
        var body = Body(264);
        var textures = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(84));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(textures + 16), 0);
        body = Append(body, textures + 8, Encoding.ASCII.GetBytes(first + "\0"), first.Length + 1);
        return Append(body, textures + 16 + 8, Encoding.ASCII.GetBytes(second + "\0"), second.Length + 1);
    }

    [Fact]
    public async Task A_legacy_model_has_undeclared_skins_and_resolves_textures_by_root_name_or_listfile()
    {
        var system = new InMemoryFileSystem();
        system.Names.Use(new Dictionary<uint, string> { [710] = "textures/listed.blp" });
        await using var files = await Open(system, Stored(ModelId, Legacy("Textures\\Root.blp", "TEXTURES\\LISTED.BLP")),
            new FakeFile(705, "textures/root.blp"), new FakeFile(710));
        var models = new BuildModels(files);
        var read = await models.ReadModelAsync(ModelId);

        Assert.Equal(ModelReadState.Read, read.State); Assert.False(read.Model!.IsChunked);
        var skin = Ref(read, ModelReferenceKind.Skin);
        Assert.Equal((ModelReferenceState.NotDeclared, (uint?)null, ModelReferenceResolution.None), (skin.State, skin.FileDataId, skin.Resolution));
        var root = Ref(read, ModelReferenceKind.Texture, 0);
        Assert.Equal(("Textures\\Root.blp", ModelReferenceResolution.RootName, ModelReferenceState.Found, (uint?)705u),
            (root.Name, root.Resolution, root.State, root.FileDataId));
        Assert.Equal(FilePathSource.RootHash, root.File!.PathSource);
        var listed = Ref(read, ModelReferenceKind.Texture, 1);
        Assert.Equal((ModelReferenceResolution.ListfileName, ModelReferenceState.Found, (uint?)710u), (listed.Resolution, listed.State, listed.FileDataId));
        Assert.Equal("textures/listed.blp", listed.File!.Path);

        var skinRead = await models.ReadSkinAsync(read, 0);
        Assert.Equal((ModelReadState.NotDeclared, (uint?)null), (skinRead.State, skinRead.FileDataId));
        Assert.Null(skinRead.Skin); Assert.Contains("no SFID", skinRead.Message);
    }

    [Fact]
    public async Task A_legacy_texture_name_nothing_maps_is_unresolved_by_name_not_missing()
    {
        await using var files = await Open(Stored(ModelId, Legacy("textures/nowhere.blp", "textures/other.blp")), new FakeFile(705, Locales: ["deDE"], Path: "textures/other.blp"));
        var read = await new BuildModels(files).ReadModelAsync(ModelId);
        var missing = Ref(read, ModelReferenceKind.Texture, 0);
        Assert.Equal((ModelReferenceState.UnresolvedByName, ModelReferenceResolution.None, (uint?)null, "textures/nowhere.blp"),
            (missing.State, missing.Resolution, missing.FileDataId, missing.Name));
        Assert.Equal(ModelReferenceState.NotInLocale, Ref(read, ModelReferenceKind.Texture, 1).State);
    }

    [Fact]
    public async Task A_whitespace_only_legacy_texture_name_is_no_file_declared_not_a_path_lookup()
    {
        await using var files = await Open(Stored(ModelId, Legacy(" ", "textures/other.blp")));
        var read = await new BuildModels(files).ReadModelAsync(ModelId);
        Assert.Equal(ModelReadState.Read, read.State);
        var blank = Ref(read, ModelReferenceKind.Texture, 0);
        Assert.Equal((ModelReferenceState.NoFileDeclared, (string?)null, ModelReferenceResolution.None), (blank.State, blank.Name, blank.Resolution));
    }

    [Fact]
    public async Task A_read_model_and_skin_carry_every_encrypted_chunk_key_decrypted_or_not()
    {
        const string Other = "00000000000000CC";
        await using var files = await Open(new FakeFile(ModelId, Content: Full(sfid: [900]), Storage: FileStorage.Stored, DecryptedKeys: [Other, Key]),
            new FakeFile(900, Content: Skin(), Storage: FileStorage.Stored, DecryptedKeys: [Key]), new FakeFile(901, Content: Full()));
        var models = new BuildModels(files);
        var model = await models.ReadModelAsync(ModelId);
        Assert.Equal(ModelReadState.Read, model.State); Assert.Equal([Key, Other], model.ChunkKeys);
        var skin = await models.ReadSkinAsync(model, 0);
        Assert.Equal(ModelReadState.Read, skin.State); Assert.Equal([Key], skin.ChunkKeys);
        Assert.Empty((await models.ReadModelAsync(901)).ChunkKeys); // a clear file has none
    }

    [Fact]
    public async Task A_legacy_texture_of_another_type_or_without_a_name_is_not_looked_up()
    {
        await using var files = await Open(Stored(ModelId, Body(264)));
        var read = await new BuildModels(files).ReadModelAsync(ModelId);
        Assert.Equal(ModelReferenceState.NoFileDeclared, Ref(read, ModelReferenceKind.Texture, 0).State);
        Assert.Equal(ModelReferenceState.ResolvedElsewhere, Ref(read, ModelReferenceKind.Texture, 1).State);
    }

    public static TheoryData<FakeFile, ModelReadState, string?> Failures => new()
    {
        { new FakeFile(ModelId, Content: Full(), ReadFailure: new(FileReadState.Missing, "OBJECT_ACCESS_MISSING", ObservedAt: DateTimeOffset.UnixEpoch)), ModelReadState.Missing, "OBJECT_ACCESS_MISSING" },
        { new FakeFile(ModelId), ModelReadState.NeedsNetwork, "OBJECT_ACCESS_NEEDS_NETWORK" },
        { new FakeFile(ModelId, Content: Full(), ReadFailure: new(FileReadState.Corrupt, "BLTE_CHECKSUM", OnStreamRead: true)), ModelReadState.Corrupt, "BLTE_CHECKSUM" },
        { new FakeFile(ModelId, Content: Full(), ReadFailure: new(FileReadState.WrongKey, "BLTE_WRONG_KEY")), ModelReadState.WrongKey, "BLTE_WRONG_KEY" },
        { new FakeFile(ModelId, Content: Full(), ReadFailure: new(FileReadState.Unavailable, "OBJECT_ACCESS_UNAVAILABLE")), ModelReadState.Unavailable, "OBJECT_ACCESS_UNAVAILABLE" },
        { new FakeFile(ModelId, NotInEncoding: true), ModelReadState.NotInEncoding, null },
        { new FakeFile(ModelId, Locales: ["deDE"]), ModelReadState.NotInLocale, null },
        { Stored(ModelId, "MD20\0\0\0\0"u8.ToArray()), ModelReadState.Rejected, "MODEL_INVALID" },
        { Stored(ModelId, "XXXX\0\0\0\0"u8.ToArray()), ModelReadState.Rejected, "MODEL_UNSUPPORTED" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Every_failure_is_its_named_state_without_a_model(FakeFile model, ModelReadState state, string? code)
    {
        await using var files = await Open(model, new FakeFile(1)); // the build needs an enUS file to open in enUS
        var read = await new BuildModels(files).ReadModelAsync(ModelId);
        Assert.Equal((state, code), (read.State, read.Code));
        Assert.Null(read.Model); Assert.Empty(read.References); Assert.Equal(0, read.MainSkinCount);
        Assert.Equal(ModelId, read.FileDataId);
        if (state == ModelReadState.Missing) Assert.Equal(DateTimeOffset.UnixEpoch, read.ObservedAt);
        if (state == ModelReadState.NotInEncoding) Assert.Equal(FileStorage.NoEncodedCopy, read.File!.Storage);
    }

    [Fact]
    public async Task An_unknown_model_is_unknown_file_by_id_and_by_path()
    {
        await using var files = await Open(new FakeFile(1));
        foreach (var (file, id) in new[] { (FileRef.Id(ModelId), (uint?)ModelId), (FileRef.ForPath("creature/none.m2"), null) })
        {
            var read = await new BuildModels(files).ReadModelAsync(file);
            Assert.Equal((ModelReadState.UnknownFile, id, (BuildFile?)null), (read.State, read.FileDataId, read.File));
        }
    }

    [Fact]
    public async Task Holes_are_partial_or_protected_with_their_key_names_and_never_parsed()
    {
        var bytes = Full();
        await using var files = await Open(
            new FakeFile(ModelId, Content: bytes, Holes: [new(8, 4, "00000000000000AA"), new(bytes.Length - 2, 2, Key)]),
            new FakeFile(ModelId + 1, Content: bytes, Holes: [new(0, bytes.Length, Key)]));
        var models = new BuildModels(files);

        var partial = await models.ReadModelAsync(ModelId);
        Assert.Equal((ModelReadState.Partial, "MODEL_SOURCE_PARTIAL"), (partial.State, partial.Code));
        Assert.Equal([Key, "00000000000000AA"], partial.KeyNames); // ordinal order
        Assert.Null(partial.Model); Assert.Empty(partial.ChunkKeys);
        var whole = await models.ReadModelAsync(ModelId + 1);
        Assert.Equal((ModelReadState.Protected, "MODEL_SOURCE_PROTECTED"), (whole.State, whole.Code));
        Assert.Equal([Key], whole.KeyNames); Assert.Null(whole.Model);
    }

    [Fact]
    public async Task A_model_over_the_source_limit_is_rejected_before_any_fetch()
    {
        var bytes = Full();
        var system = new InMemoryFileSystem();
        await using var files = await Open(system, new FakeFile(ModelId, Content: bytes));
        var read = await new BuildModels(files, new M2Limits(MaxSourceBytes: bytes.Length - 1)).ReadModelAsync(ModelId);
        Assert.Equal((ModelReadState.Rejected, "MODEL_LIMIT"), (read.State, read.Code));
        Assert.Equal(FileStorage.NotStored, (await files.StatAsync(ModelId, default)).File!.Storage);
    }

    [Fact]
    public async Task A_main_skin_reads_and_a_missing_or_undeclared_or_refused_one_is_its_state()
    {
        await using var files = await Open(Stored(ModelId, Full(sfid: [900, 901])), Stored(900, Skin()), new FakeFile(901, Content: [1, 2, 3, 4]));
        var models = new BuildModels(files);
        var model = await models.ReadModelAsync(ModelId);

        var skin = await models.ReadSkinAsync(model, 0);
        Assert.Equal((ModelReadState.Read, 0, (uint?)900u), (skin.State, skin.Profile, skin.FileDataId));
        Assert.Equal([2, 0, 1, 3], skin.Skin!.VertexLookup); Assert.Equal(3, skin.Skin.TriangleLookup.Count);
        Assert.Single(skin.Skin.Sections); Assert.Single(skin.Skin.Batches); Assert.Equal(900u, skin.File!.FileDataId);

        foreach (var profile in new[] { 1, -1, 5 })
        {
            var undeclared = await models.ReadSkinAsync(model, profile);
            Assert.Equal((ModelReadState.NotDeclared, (uint?)null), (undeclared.State, undeclared.FileDataId)); Assert.Null(undeclared.Skin);
        }

        await using var other = await Open(Stored(ModelId, Full(sfid: [900])), new FakeFile(900, Content: [1, 2, 3, 4], Storage: FileStorage.Stored));
        var refusedModel = await new BuildModels(other).ReadModelAsync(ModelId);
        var refused = await new BuildModels(other).ReadSkinAsync(refusedModel, 0);
        Assert.Equal((ModelReadState.Rejected, "MODEL_INVALID"), (refused.State, refused.Code)); Assert.Null(refused.Skin);
    }

    [Fact]
    public async Task A_skin_file_the_build_lacks_or_cannot_read_is_its_state_never_empty_geometry()
    {
        await using var files = await Open(Stored(ModelId, Full(sfid: [900]))); // 900 isn't in the build
        var models = new BuildModels(files);
        var model = await models.ReadModelAsync(ModelId);
        Assert.Equal(ModelReferenceState.UnknownFile, Ref(model, ModelReferenceKind.Skin).State);
        var skin = await models.ReadSkinAsync(model, 0);
        Assert.Equal((ModelReadState.UnknownFile, (uint?)900u), (skin.State, skin.FileDataId)); Assert.Null(skin.Skin);

        var bytes = Skin();
        await using var holed = await Open(Stored(ModelId, Full(sfid: [900])), new FakeFile(900, Content: bytes, Holes: [new(0, bytes.Length, Key)]),
            new FakeFile(901));
        var holedModels = new BuildModels(holed);
        var protectedSkin = await holedModels.ReadSkinAsync(await holedModels.ReadModelAsync(ModelId), 0);
        Assert.Equal((ModelReadState.Protected, Key), (protectedSkin.State, Assert.Single(protectedSkin.KeyNames))); Assert.Null(protectedSkin.Skin);

        await using var offline = await Open(Stored(ModelId, Full(sfid: [900])), new FakeFile(900));
        var offlineModels = new BuildModels(offline);
        var needs = await offlineModels.ReadSkinAsync(await offlineModels.ReadModelAsync(ModelId), 0);
        Assert.Equal(ModelReadState.NeedsNetwork, needs.State); Assert.Null(needs.Skin);
    }

    [Fact]
    public async Task A_skin_needs_a_model_read_with_state_read()
    {
        await using var files = await Open(new FakeFile(ModelId));
        var models = new BuildModels(files);
        var failed = await models.ReadModelAsync(ModelId);
        await Assert.ThrowsAsync<ArgumentException>(() => models.ReadSkinAsync(failed, 0));
    }

    [Fact]
    public async Task A_geometry_skin_reads_and_the_geometry_profile_refusal_is_rejected()
    {
        var plain = Chunks(("SFID", Words32(900)), ("TXID", Words32(700, 0)), ("MD21", Body()));
        await using var files = await Open(Stored(ModelId, plain), Stored(ModelId + 1, Full(sfid: [900])), Stored(900, Skin()));
        var models = new BuildModels(files);

        var geometry = await models.ReadGeometrySkinAsync(await models.ReadModelAsync(ModelId), 0);
        Assert.Equal(ModelReadState.Read, geometry.State); Assert.Equal([2, 0, 1, 3], geometry.Skin!.VertexLookup);
        var refused = await models.ReadGeometrySkinAsync(await models.ReadModelAsync(ModelId + 1), 0); // SKID 600
        Assert.Equal((ModelReadState.Rejected, "MODEL_UNSUPPORTED"), (refused.State, refused.Code)); Assert.Null(refused.Skin);
    }

    [Fact]
    public async Task Cancellation_throws_before_the_read_and_during_reference_stats()
    {
        await using var inner = await Open(Stored(ModelId, Full()));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BuildModels(inner).ReadModelAsync(ModelId, canceled.Token));

        using var during = new CancellationTokenSource();
        await using var files = new ScriptedFiles(inner, async (file, token) =>
        {
            if (file.FileDataId == ModelId) return;
            await during.CancelAsync();
            await Task.Delay(Timeout.Infinite, token);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BuildModels(files).ReadModelAsync(ModelId, during.Token));

        var model = await new BuildModels(inner).ReadModelAsync(ModelId);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BuildModels(inner).ReadSkinAsync(model, 0, canceled.Token));
    }

    [Fact]
    public async Task At_most_eight_reference_stats_run_at_once()
    {
        var ids = Enumerable.Range(1000, 40).Select(i => (uint)i).ToArray();
        var bytes = Chunks(("SFID", Words32(900)), ("TXID", Words32(700, 0)), ("RPID", Words32(ids)), ("MD21", Body()));
        await using var inner = await Open(Stored(ModelId, bytes));
        var inFlight = 0; var most = 0; var calls = 0;
        var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var files = new ScriptedFiles(inner, async (file, token) =>
        {
            if (file.FileDataId == ModelId) return;
            var now = Interlocked.Increment(ref inFlight); Interlocked.Increment(ref calls);
            lock (full) most = Math.Max(most, now);
            if (now >= BuildModels.MaxConcurrentStats) full.TrySetResult();
            try { await full.Task.WaitAsync(TimeSpan.FromSeconds(10), token); }
            finally { Interlocked.Decrement(ref inFlight); }
        });
        var read = await new BuildModels(files).ReadModelAsync(ModelId);

        Assert.Equal(ModelReadState.Read, read.State);
        Assert.Equal(BuildModels.MaxConcurrentStats, most);
        Assert.Equal(42, calls); // 40 RPID ids, the skin and the type-0 texture
        Assert.Equal(ids, read.References.Where(r => r.Kind == ModelReferenceKind.RecursiveParticleModel).Select(r => r.FileDataId!.Value));
    }

    [Fact]
    public async Task Limits_outside_the_reader_ceilings_are_refused_up_front()
    {
        await using var files = await Open(new FakeFile(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BuildModels(files, new M2Limits(MaxTextures: 0)));
        Assert.Throws<ArgumentNullException>(() => new BuildModels(null!));
    }

    // Delegates to a layer 4 handle; runs a hook before each stat (which may throw or wait).
    private sealed class ScriptedFiles(IBuildFiles inner, Func<FileRef, CancellationToken, Task> beforeStat) : IBuildFiles
    {
        public BuildRef Build => inner.Build;
        public string Locale => inner.Locale;
        public IAsyncEnumerable<BuildFile> ListAsync(FileFilter filter, CancellationToken cancellationToken) => inner.ListAsync(filter, cancellationToken);
        public async Task<FileStat> StatAsync(FileRef file, CancellationToken cancellationToken)
        {
            await beforeStat(file, cancellationToken);
            return await inner.StatAsync(file, cancellationToken);
        }
        public Task<bool> ExistsAsync(FileRef file, CancellationToken cancellationToken) => inner.ExistsAsync(file, cancellationToken);
        public Task<FileRead> GetAsync(FileRef file, CancellationToken cancellationToken) => inner.GetAsync(file, cancellationToken);
        public Task<FileRead> ReadRangeAsync(FileRef file, long offset, int length, CancellationToken cancellationToken) =>
            inner.ReadRangeAsync(file, offset, length, cancellationToken);
        public Task<FileOpen> OpenReadAsync(FileRef file, CancellationToken cancellationToken) => inner.OpenReadAsync(file, cancellationToken);
        public Task<EnsureResult> EnsureAsync(EnsureSelection selection, EnsurePriority priority, IProgress<EnsureProgress>? progress,
            CancellationToken cancellationToken) => inner.EnsureAsync(selection, priority, progress, cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
