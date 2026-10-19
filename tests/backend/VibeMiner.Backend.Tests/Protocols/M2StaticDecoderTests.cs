using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Protocols.Tact;
using Xunit;

namespace VibeMiner.Backend.Tests.Protocols;

public sealed class M2StaticDecoderTests
{
    [Theory]
    [InlineData(264)][InlineData(274)]
    public async Task Native_geometry_has_two_level_indices_and_section_bounds_without_coordinate_or_pose_guess(int version)
    {
        var fixture = Model((uint)version); using var source = new AsyncSource(fixture); var model = await M2ModelReader.ReadAsync(source);
        using var skinSource = new AsyncSource(Skin((uint)version)); var skin = await M2SkinReader.ReadAsync(skinSource, model);
        var geometry = M2StaticGeometry.Create(model, skin);
        Assert.Equal(new uint[] { 2, 0, 1 }, geometry.Indices);
        Assert.Equal(new Vector3(0, 3, 0), geometry.Vertices[2].Position);
        Assert.Equal(new Vector3(0, 0, 1), geometry.Vertices[2].Normal);
        Assert.Equal(new Vector2(0, 1), geometry.Vertices[2].Uv0);
        Assert.Equal(new Vector2(0.25f, 0.75f), geometry.Vertices[2].Uv1);
        Assert.Equal(0x04030201u, geometry.Vertices[2].BoneWeights); Assert.Equal(0x08070605u, geometry.Vertices[2].BoneIndices);
        Assert.Equal(Vector3.Zero, geometry.Bounds.Minimum); Assert.Equal(new Vector3(2, 3, 0), geometry.Bounds.Maximum);
        Assert.Equal(new Vector3(50, 60, 70), model.ComputedBounds!.Value.Maximum);
        Assert.Equal(0x03020100u, skin.BoneProperties[0]); Assert.Equal(4, skin.BoneProperties.Count);
        Assert.Equal((uint)version, model.Version); Assert.Equal(version == 274, model.IsChunked);
        Assert.Equal(fixture.Length, source.Position); Assert.False(source.Closed); Assert.False(skinSource.Closed);
    }

    [Fact]
    public async Task Reordered_modern_dependencies_keep_exact_ordinals_zero_ids_and_nested_origin()
    {
        var model = await ReadModel(Model());
        Assert.Equal(40, model.BaseOffset); Assert.Equal("authored", model.Name);
        Assert.Equal(new uint[] { 900 }, model.SkinFileDataIds); Assert.Equal(new uint[] { 901 }, model.LodSkinFileDataIds);
        Assert.Equal(700u, model.Textures[0].FileDataId); Assert.Equal(0u, model.Textures[1].FileDataId);
        Assert.Equal(11u, model.Textures[1].Type);
        Assert.Equal(new[] { "SFID", "TXID", "MD21" }, model.Chunks.Select(c => c.Id));
        var wrong = Model(); var origin = Base(wrong); U32(wrong, origin + 64, (uint)(wrong.Length - 4 * 48));
        await Throws("MODEL_INVALID", () => ReadModel(wrong));
    }

    [Fact]
    public async Task Opaque_materials_admit_exact_texture_or_unresolved_creature_type_without_slot_inference()
    {
        var model = await ReadModel(Model()); var skin = await ReadSkin(Skin(), model);
        var draw = Assert.Single(M2StaticMaterials.AdmitOpaque(model, skin).Draws);
        Assert.Equal(700u, draw.FileDataId); Assert.Equal(M2TextureReferenceKind.FileDataId, draw.ReferenceKind); Assert.True(draw.WrapU); Assert.True(draw.WrapV);
        Assert.Equal(0, draw.BatchOrdinal); Assert.Equal(0, draw.TextureOrdinal); Assert.Equal(ushort.MaxValue, model.TextureWeights[0]);
        foreach (var type in new uint[] { 11, 12, 13 })
        {
            var replacement = await ReadModel(Model(firstTextureType: type)); var replacementSkin = await ReadSkin(Skin(), replacement);
            var result = Assert.Single(M2StaticMaterials.AdmitOpaque(replacement, replacementSkin).Draws);
            Assert.Equal(type, result.ReplacementType); Assert.Null(result.FileDataId); Assert.Equal(M2TextureReferenceKind.CreatureReplacement, result.ReferenceKind);
        }
    }

    [Fact]
    public async Task Parsed_skin_cannot_be_reused_with_same_count_other_model()
    {
        var first = await ReadModel(Model()); var second = await ReadModel(Model()); var skin = await ReadSkin(Skin(), first);
        Assert.Equal("MODEL_INVALID", Assert.Throws<DiscoveryException>(() => M2StaticGeometry.Create(second, skin)).Code);
        Assert.Equal("MODEL_INVALID", Assert.Throws<DiscoveryException>(() => M2StaticMaterials.AdmitOpaque(second, skin)).Code);
    }

    [Theory]
    [InlineData(256)][InlineData(263)][InlineData(272)][InlineData(273)][InlineData(275)]
    public async Task Unsupported_versions_are_not_assumed_to_have_the_same_layout(int version)
    {
        var bytes = Model(); U32(bytes, Base(bytes) + 4, (uint)version); await Throws("MODEL_UNSUPPORTED", () => ReadModel(bytes));
    }

    [Fact]
    public async Task Version_container_pair_and_skin_header_geometry_are_explicit()
    {
        await Throws("MODEL_UNSUPPORTED", () => ReadModel(Body(274)));
        var wrapped = Chunks(("MD21", Body(264)), ("SFID", Words32(900)), ("TXID", Words32(700, 0)));
        await Throws("MODEL_UNSUPPORTED", () => ReadModel(wrapped));
        var modern = await ReadModel(Model()); var oldSkin = Skin(264);
        await Throws("MODEL_INVALID", () => ReadSkin(oldSkin, modern));
    }

    [Fact]
    public async Task Dependency_frames_duplicates_and_missing_required_modern_declarations_are_explicit()
    {
        await Throws("MODEL_INVALID", () => ReadModel(Chunks(("MD21", Body()), ("MD21", Body()))));
        await Throws("MODEL_INVALID", () => ReadModel(Chunks(("MD21", Body()), ("SFID", Words32(900)), ("SFID", Words32(901)))));
        await Throws("MODEL_UNSUPPORTED", () => ReadModel(Chunks(("SFID", Words32(900)))));
        await Throws("MODEL_UNSUPPORTED", () => ReadModel(Chunks(("MD21", Body()), ("TXID", Words32(700, 0)))));
        await Throws("MODEL_UNSUPPORTED", () => ReadModel(Chunks(("MD21", Body()), ("SFID", Words32(900)))));
        await Throws("MODEL_INVALID", () => ReadModel(Chunks(("MD21", Body()), ("SFID", new byte[] { 1 }), ("TXID", Words32(700, 0)))));
        await Throws("MODEL_INVALID", () => ReadModel(Chunks(("MD21", Body()), ("SFID", Words32(900)), ("TXID", Words32(700)))));
        var excess = Model(); U32(excess, 4, uint.MaxValue); await Throws("MODEL_INVALID", () => ReadModel(excess));
        var tail = Model().Concat(new byte[] { 1, 2, 3 }).ToArray(); await Throws("MODEL_INVALID", () => ReadModel(tail));
        var nested = Model(); nested[Base(nested)] = (byte)'X'; await Throws("MODEL_INVALID", () => ReadModel(nested));
    }

    [Theory]
    [InlineData(0)][InlineData(3)][InlineData(7)][InlineData(39)][InlineData(100)][InlineData(343)]
    public async Task Truncated_model_source_never_returns_a_ready_model(int length) =>
        await Throws(length < 4 ? "MODEL_INVALID" : null, () => ReadModel(Model()[..length]));

    [Fact]
    public async Task Name_framing_strict_utf8_empty_text_and_filename_only_declarations_do_not_fetch()
    {
        var noName = Model(); U32(noName, Base(noName) + 8, 0); Assert.Equal("", (await ReadModel(noName)).Name);
        var bad = Model(); var origin = Base(bad); var name = (int)U32(bad, origin + 12) + origin; bad[name + 8] = 1;
        await Throws("MODEL_INVALID", () => ReadModel(bad));
        bad = Model(); origin = Base(bad); name = (int)U32(bad, origin + 12) + origin; bad[name] = 0xff;
        await Throws("MODEL_UNSUPPORTED", () => ReadModel(bad));
        var legacy = await ReadModel(Model(264)); Assert.Null(legacy.Textures[0].FileDataId);
        var skin = await ReadSkin(Skin(264), legacy);
        Assert.Equal("MODEL_MATERIAL_UNSUPPORTED", Assert.Throws<DiscoveryException>(() => M2StaticMaterials.AdmitOpaque(legacy, skin)).Code);
    }

    [Theory]
    [InlineData(60, uint.MaxValue, 304)][InlineData(60, 4, uint.MaxValue)][InlineData(60, 4, 4)]
    [InlineData(80, 33, 304)][InlineData(112, 257, 304)]
    public async Task Counts_offsets_and_overflow_are_bounded_before_allocation(int at, uint count, uint offset)
    {
        var bytes = Model(); var origin = Base(bytes); U32(bytes, origin + at, count); U32(bytes, origin + at + 4, offset);
        await Throws(count is uint.MaxValue or 33 or 257 ? "MODEL_LIMIT" : "MODEL_INVALID", () => ReadModel(bytes));
    }

    [Fact]
    public async Task Header_optional_combiner_and_declared_bounds_cannot_overlap_or_invert()
    {
        var bytes = Model(); U32(bytes, Base(bytes) + 16, 8); await Throws("MODEL_INVALID", () => ReadModel(bytes));
        bytes = Model(); F(bytes, Base(bytes) + 160, 1); await Throws("MODEL_INVALID", () => ReadModel(bytes));
        bytes = Model(); F(bytes, Base(bytes) + 184, -1); await Throws("MODEL_INVALID", () => ReadModel(bytes));
    }

    [Theory]
    [InlineData(0, float.NaN)][InlineData(4, float.PositiveInfinity)][InlineData(20, float.NegativeInfinity)]
    [InlineData(32, float.NaN)][InlineData(40, float.PositiveInfinity)]
    public async Task Nonfinite_positions_normals_and_both_uv_sets_are_rejected(int relative, float value)
    { var bytes = Model(); var origin = Base(bytes); F(bytes, origin + (int)U32(bytes, origin + 64) + relative, value); await Throws("MODEL_INVALID", () => ReadModel(bytes)); }

    [Theory]
    [InlineData(4, 5)][InlineData(12, 4)][InlineData(20, 3)]
    public async Task Skin_lookup_triangles_and_four_byte_property_counts_are_checked(int headerAt, uint count)
    {
        var model = await ReadModel(Model()); var skin = Skin(); U32(skin, headerAt, count); await Throws("MODEL_INVALID", () => ReadSkin(skin, model));
    }

    [Fact]
    public async Task Both_index_levels_and_section_local_range_are_checked()
    {
        var model = await ReadModel(Model()); var skin = Skin(); U16(skin, (int)U32(skin, 8), 4); await Throws("MODEL_INVALID", () => ReadSkin(skin, model));
        skin = Skin(); U16(skin, (int)U32(skin, 16), 4); await Throws("MODEL_INVALID", () => ReadSkin(skin, model));
        skin = Skin(); var section = (int)U32(skin, 32); U16(skin, section + 6, 2); await Throws("MODEL_INVALID", () => ReadSkin(skin, model));
        skin = Skin(); section = (int)U32(skin, 32); U16(skin, section + 10, 2); await Throws("MODEL_INVALID", () => ReadSkin(skin, model));
        skin = Skin(); section = (int)U32(skin, 32); U16(skin, section + 8, 65535); await Throws("MODEL_INVALID", () => ReadSkin(skin, model));
    }

    [Fact]
    public async Task Section_high_word_is_not_lost_and_winding_is_preserved()
    {
        var model = await ReadModel(Model()); var skinBytes = Skin(triangleCount: 65_541); var section = (int)U32(skinBytes, 32);
        U16(skinBytes, section + 2, 1); U16(skinBytes, section + 8, 2); U16(skinBytes, section + 10, 3);
        var skin = await ReadSkin(skinBytes, model); Assert.Equal(65_538u, skin.Sections[0].TriangleStart);
        var geometry = M2StaticGeometry.Create(model, skin); Assert.Equal(new uint[] { 2, 0, 1 }, geometry.Indices.Skip(65_538));
    }

    [Theory]
    [InlineData(4, 1)][InlineData(10, 1)][InlineData(8, 0)][InlineData(16, 1)][InlineData(18, 1)][InlineData(20, 1)][InlineData(22, 1)]
    public async Task All_batch_section_material_color_and_combo_references_must_exist(int relative, ushort value)
    {
        var model = await ReadModel(Model()); var skin = Skin(); U16(skin, (int)U32(skin, 40) + relative, value);
        await Throws("MODEL_INVALID", () => ReadSkin(skin, model));
    }

    [Fact]
    public async Task Empty_lookup_is_not_a_default_and_active_channels_are_not_sampled()
    {
        var bytes = Model(); U32(bytes, Base(bytes) + 144, 0); var model = await ReadModel(bytes);
        await Throws("MODEL_INVALID", () => ReadSkin(Skin(), model));
        foreach (var active in new[] { "weight", "transform" })
        {
            bytes = Model(activeChannels: true); var origin = Base(bytes); var at = (int)U32(bytes, origin + (active == "weight" ? 148 : 156)); U16(bytes, origin + at, 0);
            model = await ReadModel(bytes); var skin = await ReadSkin(Skin(), model);
            Assert.Equal("MODEL_MATERIAL_UNSUPPORTED", Assert.Throws<DiscoveryException>(() => M2StaticMaterials.AdmitOpaque(model, skin)).Code);
        }
    }

    [Theory]
    [InlineData(0, 1)][InlineData(1, 1)][InlineData(2, 16)][InlineData(6, 1)][InlineData(12, 1)]
    public async Task Valid_but_unsupported_batch_semantics_never_receive_a_generic_material(int at, ushort value)
    {
        var model = await ReadModel(Model()); var bytes = Skin(); var batch = (int)U32(bytes, 40);
        if (at < 2) bytes[batch + at] = (byte)value; else U16(bytes, batch + at, value);
        var skin = await ReadSkin(bytes, model); Assert.Equal("MODEL_MATERIAL_UNSUPPORTED", Assert.Throws<DiscoveryException>(() => M2StaticMaterials.AdmitOpaque(model, skin)).Code);
        Assert.Equal(3, M2StaticGeometry.Create(model, skin).Indices.Count);
    }

    [Theory]
    [InlineData(0, 1)][InlineData(2, 1)]
    public async Task Material_flags_and_blend_modes_are_not_reinterpreted(int at, ushort value)
    {
        var bytes = Model(); var origin = Base(bytes); U16(bytes, origin + (int)U32(bytes, origin + 116) + at, value);
        var model = await ReadModel(bytes); var skin = await ReadSkin(Skin(), model);
        Assert.Equal("MODEL_MATERIAL_UNSUPPORTED", Assert.Throws<DiscoveryException>(() => M2StaticMaterials.AdmitOpaque(model, skin)).Code);
    }

    [Theory]
    [InlineData(1)][InlineData(4)][InlineData(14)][InlineData(99)]
    public async Task Replacement_types_never_guess_a_creature_slot_or_type14_fourth_slot(uint type)
    {
        var model = await ReadModel(Model(firstTextureType: type)); var skin = await ReadSkin(Skin(), model);
        Assert.Equal("MODEL_MATERIAL_UNSUPPORTED", Assert.Throws<DiscoveryException>(() => M2StaticMaterials.AdmitOpaque(model, skin)).Code);
    }

    [Theory]
    [InlineData(0f)][InlineData(float.Epsilon)][InlineData(float.MaxValue)]
    public async Task Unusable_lit_normals_remain_raw_but_block_material_admission(float magnitude)
    {
        var bytes = Model(); var origin = Base(bytes); var vertex = origin + (int)U32(bytes, origin + 64); F(bytes, vertex + 20, magnitude); F(bytes, vertex + 28, 0);
        var model = await ReadModel(bytes); var skin = await ReadSkin(Skin(), model); Assert.Equal(3, M2StaticGeometry.Create(model, skin).Indices.Count);
        Assert.Equal("MODEL_MATERIAL_UNSUPPORTED", Assert.Throws<DiscoveryException>(() => M2StaticMaterials.AdmitOpaque(model, skin)).Code);
    }

    [Fact]
    public async Task Unknown_chunks_and_shadow_batches_are_preserved_but_block_appearance()
    {
        var bytes = Model().Concat(Chunks(("TXAC", new byte[] { 1, 2 }))).ToArray(); var model = await ReadModel(bytes); var skin = await ReadSkin(Skin(), model);
        Assert.Equal("TXAC", model.Chunks[^1].Id); Assert.Equal("MODEL_MATERIAL_UNSUPPORTED", Assert.Throws<DiscoveryException>(() => M2StaticMaterials.AdmitOpaque(model, skin)).Code);
        model = await ReadModel(Model()); skin = await ReadSkin(Skin(shadows: true), model); Assert.Single(skin.ShadowBatches);
        Assert.Equal("MODEL_MATERIAL_UNSUPPORTED", Assert.Throws<DiscoveryException>(() => M2StaticMaterials.AdmitOpaque(model, skin)).Code);
    }

    [Fact]
    public async Task Batches_are_preserved_in_order_and_layers_are_never_silently_dropped()
    {
        var model = await ReadModel(Model()); var skin = await ReadSkin(Skin(twoBatches: true), model); Assert.Equal(2, skin.Batches.Count);
        Assert.Equal(0, skin.Batches[0].Priority); Assert.Equal(-3, skin.Batches[1].Priority);
        Assert.Equal("MODEL_MATERIAL_UNSUPPORTED", Assert.Throws<DiscoveryException>(() => M2StaticMaterials.AdmitOpaque(model, skin)).Code);
        model = await ReadModel(Model(twoCombos: true)); var bytes = Skin(); U16(bytes, (int)U32(bytes, 40) + 14, 2); skin = await ReadSkin(bytes, model);
        Assert.Equal("MODEL_MATERIAL_UNSUPPORTED", Assert.Throws<DiscoveryException>(() => M2StaticMaterials.AdmitOpaque(model, skin)).Code);
    }

    [Fact]
    public async Task Caller_bounds_are_checked_before_payload_reads_and_combined_assembly_allocation()
    {
        using var huge = new AsyncSource(Model(), reportedLength: (64L << 20) + 1); await Throws("MODEL_LIMIT", () => M2ModelReader.ReadAsync(huge)); Assert.Equal(0, huge.Reads);
        await Throws("MODEL_LIMIT", () => ReadModel(Model(), new(MaxOutputBytes: 100)));
        await Throws("MODEL_LIMIT", () => ReadModel(Model(), new(MaxVertices: 3)));
        await Throws("MODEL_LIMIT", () => ReadModel(Model(), new(MaxChunks: 2)));
        var model = await ReadModel(Model()); await Throws("MODEL_LIMIT", () => ReadSkin(Skin(), model, new(MaxOutputBytes: 100)));
        await Throws("MODEL_LIMIT", () => ReadSkin(Skin(), model, new(MaxTriangleIndices: 3, MaxVertexLookup: 3)));
    }

    [Fact]
    public async Task Cancellation_during_buffered_geometry_read_and_before_assembly_keeps_caller_streams_open()
    {
        using var cts = new CancellationTokenSource(); using var source = new AsyncSource(Model(vertices: 4000), cancel: cts, cancelAtRead: 7);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => M2ModelReader.ReadAsync(source, token: cts.Token)); Assert.False(source.Closed);
        using var pre = new CancellationTokenSource(); pre.Cancel(); using var neverRead = new AsyncSource(Model());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => M2ModelReader.ReadAsync(neverRead, token: pre.Token)); Assert.Equal(0, neverRead.Reads);
        var model = await ReadModel(Model()); var skin = await ReadSkin(Skin(), model);
        Assert.ThrowsAny<OperationCanceledException>(() => M2StaticGeometry.Create(model, skin, pre.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => M2StaticMaterials.AdmitOpaque(model, skin, pre.Token));
        using var skinCts = new CancellationTokenSource();
        using var skinStream = new AsyncSource(Skin(), cancel: skinCts, cancelAtRead: 3);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => M2SkinReader.ReadAsync(skinStream, model, token: skinCts.Token)); Assert.False(skinStream.Closed);
    }

    [Fact]
    public async Task Readable_seekable_zero_position_and_source_ownership_are_required()
    {
        using var moved = new AsyncSource(Model()); moved.Position = 1;
        await Assert.ThrowsAsync<ArgumentException>(() => M2ModelReader.ReadAsync(moved)); Assert.Equal(0, moved.Reads);
        using var stream = new AsyncSource(Model(), seekable: false);
        await Assert.ThrowsAsync<ArgumentException>(() => M2ModelReader.ReadAsync(stream)); Assert.False(stream.Closed);
        using var truncated = new AsyncSource(Model(), reportedLength: 10000);
        await Throws("MODEL_INVALID", () => M2ModelReader.ReadAsync(truncated)); Assert.False(truncated.Closed);
    }

    [Fact]
    public async Task Material_output_is_charged_after_successful_tightly_bounded_skin_parse()
    {
        var model = await ReadModel(Model()); var bytes = Skin(); var low = 1L; var high = 65_536L;
        while (low < high)
        {
            var mid = (low + high) / 2;
            try { await ReadSkin(bytes, model, new(MaxOutputBytes: mid)); high = mid; }
            catch (DiscoveryException e) when (e.Code == "MODEL_LIMIT") { low = mid + 1; }
        }
        var skin = await ReadSkin(bytes, model, new(MaxOutputBytes: low));
        Assert.Equal(3, M2StaticGeometry.Create(model, skin).Indices.Count); // Its uint indices were reserved during parse.
        Assert.Equal("MODEL_LIMIT", Assert.Throws<DiscoveryException>(() => M2StaticMaterials.AdmitOpaque(model, skin)).Code);
        var roomy = await ReadSkin(bytes, model, new(MaxOutputBytes: low + 1024));
        Assert.Single(M2StaticMaterials.AdmitOpaque(model, roomy).Draws);
    }

    [Fact]
    public async Task Partial_projections_are_rejected_even_when_the_hole_is_not_needed_by_geometry()
    {
        using var backing = new MemoryStream(Model());
        using var source = new BlteProjectionStream(backing, new[] { new BlteUnavailableRange(backing.Length - 1, 1, 0, "0123456789ABCDEF") });
        await Throws("MODEL_UNSUPPORTED", () => M2ModelReader.ReadAsync(source)); Assert.Equal(0, backing.Position);
    }

    [Fact]
    public async Task Dependency_metadata_preserves_skeleton_bone_and_animation_identity_without_playback()
    {
        var tail = Chunks(("SKID", Words32(800)), ("BFID", Words32(0, 801)), ("AFID", new byte[] { 5, 0, 2, 0, 0x22, 3, 0, 0 }));
        var model = await ReadModel(Model().Concat(tail).ToArray());
        Assert.Equal(800u, model.SkeletonFileDataId); Assert.Equal(new uint[] { 0, 801 }, model.BoneFileDataIds);
        Assert.Equal(new M2AnimationFile(5, 2, 802), Assert.Single(model.AnimationFiles));
        var skin = await ReadSkin(Skin(), model); Assert.Equal("MODEL_MATERIAL_UNSUPPORTED", Assert.Throws<DiscoveryException>(() => M2StaticMaterials.AdmitOpaque(model, skin)).Code);
        await Throws("MODEL_INVALID", () => ReadModel(Model().Concat(Chunks(("SKID", Words32(1, 2)))).ToArray()));
        await Throws("MODEL_INVALID", () => ReadModel(Model().Concat(Chunks(("AFID", new byte[] { 1 }))).ToArray()));
    }

    [Theory]
    [InlineData(0)][InlineData(1)]
    public async Task Missing_type0_id_or_unknown_texture_flags_does_not_substitute_an_image(int mode)
    {
        var bytes = Model(); var origin = Base(bytes);
        if (mode == 0) U32(bytes, 24, 0); // TXID first entry, not its original zero replacement entry.
        else U32(bytes, origin + (int)U32(bytes, origin + 84) + 4, 4);
        var model = await ReadModel(bytes); var skin = await ReadSkin(Skin(), model);
        Assert.Equal("MODEL_MATERIAL_UNSUPPORTED", Assert.Throws<DiscoveryException>(() => M2StaticMaterials.AdmitOpaque(model, skin)).Code);
    }

    [Fact]
    public async Task Environment_coordinates_unknown_global_flags_and_effect_declarations_block_material_only()
    {
        var bytes = Model(); var origin = Base(bytes); U16(bytes, origin + (int)U32(bytes, origin + 140), ushort.MaxValue);
        var model = await ReadModel(bytes); var skin = await ReadSkin(Skin(), model);
        Assert.Equal("MODEL_MATERIAL_UNSUPPORTED", Assert.Throws<DiscoveryException>(() => M2StaticMaterials.AdmitOpaque(model, skin)).Code);
        bytes = Model(); U32(bytes, Base(bytes) + 16, 1); model = await ReadModel(bytes); skin = await ReadSkin(Skin(), model);
        Assert.Equal(3, M2StaticGeometry.Create(model, skin).Indices.Count);
        Assert.Equal("MODEL_MATERIAL_UNSUPPORTED", Assert.Throws<DiscoveryException>(() => M2StaticMaterials.AdmitOpaque(model, skin)).Code);
        var body = Append(Body(), 296, new byte[] { 1 }, 1); model = await ReadModel(Modern(body)); skin = await ReadSkin(Skin(), model);
        Assert.Equal(0, model.Declarations.Single(d => d.Name == "particles").Stride);
        Assert.Equal("MODEL_MATERIAL_UNSUPPORTED", Assert.Throws<DiscoveryException>(() => M2StaticMaterials.AdmitOpaque(model, skin)).Code);
    }

    [Fact]
    public async Task Unused_declared_channels_do_not_get_sampled_or_mark_the_model_animated()
    {
        var model = await ReadModel(Model(activeChannels: true)); var skin = await ReadSkin(Skin(), model);
        Assert.Single(M2StaticMaterials.AdmitOpaque(model, skin).Draws);
    }

    [Fact]
    public async Task Finite_bone_metadata_is_native_but_billboard_and_invalid_parent_rules_are_explicit()
    {
        var bone = new byte[88]; U16(bone, 8, ushort.MaxValue); F(bone, 76, 9);
        var model = await ReadModel(Modern(Append(Body(), 44, bone, 1))); Assert.Equal(new Vector3(9, 0, 0), model.Bones[0].Pivot);
        var skin = await ReadSkin(Skin(), model); Assert.Single(M2StaticMaterials.AdmitOpaque(model, skin).Draws);
        U32(bone, 4, 8); model = await ReadModel(Modern(Append(Body(), 44, bone, 1))); skin = await ReadSkin(Skin(), model);
        Assert.Equal("MODEL_MATERIAL_UNSUPPORTED", Assert.Throws<DiscoveryException>(() => M2StaticMaterials.AdmitOpaque(model, skin)).Code);
        U16(bone, 8, 1); await Throws("MODEL_INVALID", () => ReadModel(Modern(Append(Body(), 44, bone, 1))));
    }

    [Fact]
    public async Task Extra_header_descriptor_and_empty_geometry_remain_truthful()
    {
        var body = new byte[312]; "MD20"u8.CopyTo(body); U32(body, 4, 274); U32(body, 16, 8);
        var model = await ReadModel(Chunks(("MD21", body)));
        Assert.Equal(0u, model.Declarations.Single(d => d.Name == "combiners").Count); Assert.Null(model.ComputedBounds);
        var empty = new byte[56]; "SKIN"u8.CopyTo(empty); var skin = await ReadSkin(empty, model);
        Assert.Equal("MODEL_UNSUPPORTED", Assert.Throws<DiscoveryException>(() => M2StaticGeometry.Create(model, skin)).Code);
        Assert.Equal("MODEL_MATERIAL_UNSUPPORTED", Assert.Throws<DiscoveryException>(() => M2StaticMaterials.AdmitOpaque(model, skin)).Code);
    }

    [Fact]
    public async Task Truncated_skin_required_ranges_and_shadow_descriptor_are_not_ready()
    {
        var model = await ReadModel(Model()); var bytes = Skin(shadows: true);
        foreach (var length in new[] { 0, 3, 47, 55, bytes.Length - 1 }) await Throws("MODEL_INVALID", () => ReadSkin(bytes[..length], model));
    }

    private static byte[] Modern(byte[] body) => Chunks(("SFID", Words32(900, 901)), ("TXID", Words32(700, 0)), ("MD21", body));
    private static byte[] Append(byte[] body, int at, byte[] payload, int count)
    { var result = body.Concat(payload).ToArray(); U32(result, at, (uint)count); U32(result, at + 4, (uint)body.Length); return result; }

    private static async Task<M2Model> ReadModel(byte[] bytes, M2Limits? limits = null)
    { using var input = new AsyncSource(bytes); return await M2ModelReader.ReadAsync(input, limits); }
    private static async Task<M2Skin> ReadSkin(byte[] bytes, M2Model model, M2Limits? limits = null)
    { using var input = new AsyncSource(bytes); return await M2SkinReader.ReadAsync(input, model, limits); }
    private static async Task Throws(string? code, Func<Task> action)
    { var exception = await Assert.ThrowsAsync<DiscoveryException>(action); if (code is not null) Assert.Equal(code, exception.Code); }

    // Authored files: offsets are explicit native-format facts, with deliberately asymmetric geometry.
    private static byte[] Model(uint version = 274, uint firstTextureType = 0, bool activeChannels = false, bool twoCombos = false, int vertices = 4)
    {
        var body = Body(version, firstTextureType, activeChannels, twoCombos, vertices);
        return version == 264 ? body : Chunks(("SFID", Words32(900, 901)), ("TXID", Words32(700, 0)), ("MD21", body));
    }
    private static byte[] Body(uint version = 274, uint firstTextureType = 0, bool activeChannels = false, bool twoCombos = false, int vertices = 4)
    {
        using var output = new MemoryStream(); output.SetLength(304); output.Position = 304; using var w = new BinaryWriter(output, Encoding.UTF8, true);
        var descriptors = new List<(int At, int Count, int Offset)>();
        void A(int at, int count, Action<BinaryWriter> write) { descriptors.Add((at, count, (int)output.Position)); write(w); }
        A(8, 9, x => x.Write(Encoding.UTF8.GetBytes("authored\0")));
        A(60, vertices, x => { for (var i = 0; i < vertices; i++) { var p = i switch { 0 => Vector3.Zero, 1 => new Vector3(2, 0, 0), 2 => new Vector3(0, 3, 0), _ => new Vector3(50, 60, 70) }; x.Write(p.X); x.Write(p.Y); x.Write(p.Z); x.Write(0x04030201u); x.Write(0x08070605u); x.Write(0f); x.Write(0f); x.Write(1f); x.Write(i == 1 ? 1f : 0f); x.Write(i == 2 ? 1f : 0f); x.Write(0.25f); x.Write(0.75f); } });
        A(80, 2, x => { x.Write(firstTextureType); x.Write(3u); x.Write(0L); x.Write(11u); x.Write(0u); x.Write(0L); });
        A(112, 1, x => x.Write(0u));
        A(128, twoCombos ? 2 : 1, x => { x.Write((ushort)0); if (twoCombos) x.Write((ushort)1); });
        A(136, twoCombos ? 2 : 1, x => { x.Write((ushort)0); if (twoCombos) x.Write((ushort)0); });
        A(144, 1, x => x.Write(ushort.MaxValue));
        A(152, twoCombos ? 2 : 1, x => { x.Write(ushort.MaxValue); if (twoCombos) x.Write(ushort.MaxValue); });
        if (activeChannels) { A(72, 1, x => x.Write(new byte[40])); A(88, 1, x => x.Write(new byte[20])); A(96, 1, x => x.Write(new byte[60])); }
        var bytes = output.ToArray(); "MD20"u8.CopyTo(bytes); U32(bytes, 4, version); U32(bytes, 68, 1);
        foreach (var d in descriptors) { U32(bytes, d.At, (uint)d.Count); U32(bytes, d.At + 4, (uint)d.Offset); } return bytes;
    }
    private static byte[] Skin(uint version = 274, int triangleCount = 3, bool shadows = false, bool twoBatches = false)
    {
        var header = version == 264 ? 48 : 56; using var output = new MemoryStream(); output.SetLength(header); output.Position = header;
        using var w = new BinaryWriter(output, Encoding.UTF8, true); var ds = new List<(int At, int Count, int Offset)>();
        void A(int at, int count, Action<BinaryWriter> write) { ds.Add((at, count, (int)output.Position)); write(w); }
        A(4, 4, x => { foreach (var v in new ushort[] { 2, 0, 1, 3 }) x.Write(v); });
        A(12, triangleCount, x => { for (var i = 0; i < triangleCount; i++) x.Write((ushort)(i % 3)); });
        A(20, 4, x => { for (var i = 0; i < 4; i++) x.Write(0x03020100u); });
        A(28, 1, x => { var s = new byte[48]; U16(s, 6, 4); U16(s, 10, 3); x.Write(s); });
        A(36, twoBatches ? 2 : 1, x => { var batch = new byte[24]; U16(batch, 8, ushort.MaxValue); U16(batch, 14, 1); x.Write(batch); if (twoBatches) { batch[1] = unchecked((byte)-3); x.Write(batch); } });
        if (shadows && header == 56) A(48, 1, x => x.Write(new byte[12]));
        var bytes = output.ToArray(); "SKIN"u8.CopyTo(bytes); foreach (var d in ds) { U32(bytes, d.At, (uint)d.Count); U32(bytes, d.At + 4, (uint)d.Offset); } return bytes;
    }
    private static byte[] Chunks(params (string Id, byte[] Payload)[] chunks)
    { using var output = new MemoryStream(); using var w = new BinaryWriter(output); foreach (var c in chunks) { w.Write(Encoding.ASCII.GetBytes(c.Id)); w.Write((uint)c.Payload.Length); w.Write(c.Payload); } return output.ToArray(); }
    private static byte[] Words32(params uint[] words) { var bytes = new byte[words.Length * 4]; for (var i = 0; i < words.Length; i++) U32(bytes, i * 4, words[i]); return bytes; }
    private static int Base(byte[] bytes)
    { if (bytes.AsSpan(0, 4).SequenceEqual("MD20"u8)) return 0; for (var at = 0; at < bytes.Length; at += checked((int)U32(bytes, at + 4) + 8)) if (bytes.AsSpan(at, 4).SequenceEqual("MD21"u8)) return at + 8; throw new InvalidOperationException(); }
    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
    private static void U32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
    private static void U16(byte[] bytes, int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), value);
    private static void F(byte[] bytes, int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), value);
    private sealed class AsyncSource(byte[] bytes, long? reportedLength = null, bool seekable = true, CancellationTokenSource? cancel = null, int cancelAtRead = int.MaxValue) : Stream
    {
        private readonly MemoryStream inner = new(bytes); public int Reads { get; private set; } public bool Closed { get; private set; }
        public override bool CanRead => true; public override bool CanSeek => seekable; public override bool CanWrite => false;
        public override long Length => reportedLength ?? inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override async ValueTask<int> ReadAsync(Memory<byte> target, CancellationToken token = default)
        { if (++Reads == cancelAtRead) cancel?.Cancel(); token.ThrowIfCancellationRequested(); return await inner.ReadAsync(target[..Math.Min(997, target.Length)], token); }
        public override int Read(byte[] b, int o, int c) => throw new InvalidOperationException("Synchronous source reads are forbidden.");
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Closed = true; if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
