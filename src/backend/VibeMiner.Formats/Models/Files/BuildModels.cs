using System.Buffers.Binary;
using VibeMiner.Files;
using static VibeMiner.Formats.Models.M2Input;

namespace VibeMiner.Formats.Models;

/// <summary>
/// How reading a model or skin through layer 4 ended. <see cref="Read"/> has a parsed file. The states from
/// <see cref="UnknownFile"/> to <see cref="TooLarge"/> are layer 4's <see cref="FileReadState"/> of the same name (Missing is
/// layer 2's dated 404, never proof of absence). <see cref="Protected"/> means every byte is in holes, <see cref="Partial"/> some:
/// models and skins are never read with holes, and both carry the TACT key names. <see cref="Rejected"/> means the bytes were
/// read but the reader refused them; the code is its MODEL_* code. <see cref="NotDeclared"/> is for skins only: the model
/// declares no file for that profile (no such main profile, no SFID, or a zero id), and no file name is ever guessed.
/// </summary>
public enum ModelReadState
{
    Read,
    UnknownFile,
    NotInLocale,
    NotInEncoding,
    Missing,
    NeedsNetwork,
    Unavailable,
    Corrupt,
    Unsupported,
    WrongKey,
    StoreFailed,
    TooLarge,
    Protected,
    Partial,
    Rejected,
    NotDeclared,
}

/// <summary>What a model reference points at. SFID entries past the header's view count are <see cref="LodSkin"/>.</summary>
public enum ModelReferenceKind
{
    Skin,
    LodSkin,
    Texture,
    Skeleton,
    BoneFile,
    AnimationFile,
    PhysicsFile,
    /// <summary>An RPID model file id.</summary>
    RecursiveParticleModel,
    /// <summary>A GPID model file id.</summary>
    GeometryParticleModel,
}

/// <summary>
/// How a reference was looked up. <see cref="RootName"/> and <see cref="ListfileName"/> are lookups by path:
/// <see cref="RootName"/> when ROOT's name hash matched and the listfile doesn't name the file, <see cref="ListfileName"/> when
/// the community listfile (D105) names it (ROOT's hash may also have matched; layer 4 reports the listfile name first).
/// <see cref="None"/>: not looked up, or a path that nothing named.
/// </summary>
public enum ModelReferenceResolution
{
    None,
    FileDataId,
    RootName,
    ListfileName,
}

/// <summary>
/// One reference's state. <see cref="Found"/>, <see cref="NotInEncoding"/>, <see cref="UnknownFile"/> and
/// <see cref="NotInLocale"/> are layer 4's stat answer. A zero id is never looked up: <see cref="NoExternalFile"/> for
/// SKID, BFID, AFID, PFID, RPID and GPID; <see cref="NoFileDeclared"/> for a type-0 texture or a skin, which should have a file;
/// <see cref="ResolvedElsewhere"/> for a texture of another type (11-13 from the creature display, 15-17 guild emblem and banner, others from
/// character or item data), never counted missing. <see cref="NotDeclared"/> is a legacy MD20 skin: it has no SFID and no
/// name is guessed. <see cref="UnresolvedByName"/> is a legacy texture name neither ROOT nor the listfile maps, not proof the
/// file is absent. <see cref="CheckFailed"/> means the stat threw (a layer 2 failure); the message says why.
/// </summary>
public enum ModelReferenceState
{
    Found,
    NotInEncoding,
    UnknownFile,
    NotInLocale,
    NoExternalFile,
    NoFileDeclared,
    ResolvedElsewhere,
    NotDeclared,
    UnresolvedByName,
    CheckFailed,
}

/// <summary>
/// A file a model refers to. <see cref="Ordinal"/> is the position within its kind (a texture's ordinal, a main skin's profile,
/// a LOD skin's place among the LODs). <see cref="FileDataId"/> is the declared id, or null for a legacy name
/// (<see cref="Name"/>) or a legacy skin. <see cref="File"/> is the stat's file for Found and NotInEncoding.
/// <see cref="TextureType"/> is set for textures, <see cref="AnimationId"/> and <see cref="Variation"/> for animation files.
/// </summary>
public sealed record ModelReference(ModelReferenceKind Kind, int Ordinal, uint? FileDataId, string? Name, ModelReferenceResolution Resolution,
    ModelReferenceState State, BuildFile? File, string? Message = null, uint? TextureType = null, ushort? AnimationId = null, ushort? Variation = null);

/// <summary>
/// The outcome of <see cref="BuildModels.ReadModelAsync"/>. <see cref="Model"/> and <see cref="References"/> are set only when
/// <see cref="State"/> is Read; a failure never carries geometry. <see cref="FileDataId"/> and <see cref="File"/> come from the
/// model's stat (<see cref="File"/> also for NotInEncoding; <see cref="FileDataId"/> is the id asked for when the stat has no file). <see cref="KeyNames"/> are the holes' TACT keys for Protected and
/// Partial. <see cref="Code"/>, <see cref="Message"/> and <see cref="ObservedAt"/> explain a failure (layer 4's, or the reader's
/// MODEL_* code for Rejected).
/// </summary>
public sealed record ModelRead(ModelReadState State, uint? FileDataId, BuildFile? File, M2Model? Model, IReadOnlyList<ModelReference> References,
    string? Code = null, string? Message = null, DateTimeOffset? ObservedAt = null, IReadOnlyList<string>? KeyNames = null,
    IReadOnlyList<string>? ChunkKeys = null)
{
    public IReadOnlyList<string> KeyNames { get; init; } = KeyNames ?? [];

    /// <summary>The key names of every encrypted chunk of the model, decrypted or a hole (layer 4's
    /// <see cref="FileReadStream.ChunkKeys"/>, D350); empty unless <see cref="State"/> is Read.</summary>
    public IReadOnlyList<string> ChunkKeys { get; init; } = ChunkKeys ?? [];

    /// <summary>The header's view count: the main skins. A legacy model's skins are all <see cref="ModelReferenceState.NotDeclared"/>.</summary>
    public int MainSkinCount => (int)(Model?.ViewCount ?? 0);

    /// <summary>SFID entries past the view count. Nonzero exactly when the SFID count and the header's view count disagree.</summary>
    public int LodSkinCount => Model?.LodSkinFileDataIds.Count ?? 0;
}

/// <summary>
/// The outcome of <see cref="BuildModels.ReadSkinAsync"/> or <see cref="BuildModels.ReadGeometrySkinAsync"/>. <see cref="Skin"/>
/// is set only when <see cref="State"/> is Read; a missing or refused skin never comes back as empty geometry.
/// <see cref="FileDataId"/> is the SFID id (null when not declared), <see cref="File"/> the model read's stat of it.
/// </summary>
public sealed record SkinRead<TSkin>(ModelReadState State, int Profile, uint? FileDataId, BuildFile? File, TSkin? Skin,
    string? Code = null, string? Message = null, DateTimeOffset? ObservedAt = null, IReadOnlyList<string>? KeyNames = null,
    IReadOnlyList<string>? ChunkKeys = null) where TSkin : class
{
    public IReadOnlyList<string> KeyNames { get; init; } = KeyNames ?? [];

    /// <summary>The key names of every encrypted chunk of the skin, decrypted or a hole (layer 4's
    /// <see cref="FileReadStream.ChunkKeys"/>, D350); empty unless <see cref="State"/> is Read.</summary>
    public IReadOnlyList<string> ChunkKeys { get; init; } = ChunkKeys ?? [];
}

/// <summary>
/// Reads M2 models and their skins from one layer 4 handle, and resolves what a model refers to by file id (by name only for a
/// legacy MD20's embedded texture names). Never throws for a file or its bytes: a lookup or read state, a mid-stream read failure
/// and a reader refusal all come back as the state. Throws only for bad arguments, a disposed handle, cancellation and a
/// layer 2 failure on the model's own stat. Reference stats run at most <see cref="MaxConcurrentStats"/> at once; one that
/// throws is <see cref="ModelReferenceState.CheckFailed"/>, not a failed read. Nothing is fetched for references: only the
/// model and skin reads fetch, through layer 4.
/// </summary>
public sealed class BuildModels
{
    /// <summary>The most reference stats in flight at once.</summary>
    public const int MaxConcurrentStats = 8;

    private readonly IBuildFiles files;
    private readonly M2Limits limits;

    public BuildModels(IBuildFiles files, M2Limits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        limits ??= new();
        CheckLimits(limits);
        this.files = files; this.limits = limits;
    }

    /// <summary>
    /// Stats <paramref name="model"/>, reads it whole through one stream, then stats every nonzero reference. A file over
    /// <see cref="M2Limits.MaxSourceBytes"/> by its declared size is Rejected (MODEL_LIMIT) before any fetch. PFID (exactly one
    /// id), RPID and GPID (ids, a multiple of 4 bytes) are read from the model's chunk list; a bad length is Rejected (MODEL_INVALID).
    /// </summary>
    public async Task<ModelRead> ReadModelAsync(FileRef model, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var stat = await files.StatAsync(model, token).ConfigureAwait(false);
        if (stat.State != FileLookupState.Found || stat.File is not { } file)
            return new(StateOf(stat.State), stat.File?.FileDataId ?? model.FileDataId, stat.File, null, [], null, $"The model is {stat.State}.");
        if (file.Size > limits.MaxSourceBytes)
            return new(ModelReadState.Rejected, file.FileDataId, file, null, [], "MODEL_LIMIT",
                $"The model is {N(file.Size.Value)} bytes; models read at most {N(limits.MaxSourceBytes)}.");
        var read = await ReadFileAsync(file.FileDataId, async stream =>
        {
            var parsed = await M2ModelReader.ReadAsync(stream, limits, token).ConfigureAwait(false);
            var extra = await ExtraIdsAsync(stream, parsed, token).ConfigureAwait(false);
            return (parsed, extra);
        }, token).ConfigureAwait(false);
        if (read.State != ModelReadState.Read)
            return new(read.State, file.FileDataId, file, null, [], read.Code, read.Message, read.ObservedAt, read.KeyNames);
        var (m2, ids) = read.Value;
        var references = await ResolveAsync(Declared(m2, ids), token).ConfigureAwait(false);
        return new(ModelReadState.Read, file.FileDataId, file, m2, references, ChunkKeys: read.ChunkKeys);
    }

    /// <summary>
    /// Reads main skin <paramref name="profile"/> of a model read with <see cref="ModelReadState.Read"/> with
    /// <see cref="M2SkinReader"/>: vertex lookup, indices, sections and batches (texture units). A profile outside the view count,
    /// a legacy model (no SFID) and a zero id are NotDeclared, and nothing is read.
    /// </summary>
    public Task<SkinRead<M2Skin>> ReadSkinAsync(ModelRead model, int profile, CancellationToken token = default) =>
        ReadSkinCoreAsync(model, profile, (stream, m2) => M2SkinReader.ReadAsync(stream, m2, limits, token), token);

    /// <summary>As <see cref="ReadSkinAsync"/>, over <see cref="M2GeometrySkinReader"/> (geometry only, no material records).</summary>
    public Task<SkinRead<M2GeometrySkin>> ReadGeometrySkinAsync(ModelRead model, int profile, CancellationToken token = default) =>
        ReadSkinCoreAsync(model, profile, (stream, m2) => M2GeometrySkinReader.ReadAsync(stream, m2, limits, token), token);

    private async Task<SkinRead<TSkin>> ReadSkinCoreAsync<TSkin>(ModelRead model, int profile, Func<Stream, M2Model, Task<TSkin>> parse,
        CancellationToken token) where TSkin : class
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.State != ModelReadState.Read || model.Model is not { } m2)
            throw new ArgumentException("Skins are read from a model read with state Read.", nameof(model));
        token.ThrowIfCancellationRequested();
        var reference = model.References.FirstOrDefault(r => r.Kind == ModelReferenceKind.Skin && r.Ordinal == profile);
        if (reference?.FileDataId is not ({ } id and not 0))
            return new(ModelReadState.NotDeclared, profile, null, null, null, null,
                !m2.IsChunked ? "The legacy model declares no skin files (no SFID)."
                : profile < 0 || profile >= m2.ViewCount ? $"The model has {m2.ViewCount} main skin profiles; {profile} isn't one."
                : $"The model declares no file for skin profile {profile}.");
        var read = await ReadFileAsync(id, stream => parse(stream, m2), token).ConfigureAwait(false);
        return new(read.State, profile, id, reference.File, read.State == ModelReadState.Read ? read.Value : null,
            read.Code, read.Message, read.ObservedAt, read.KeyNames, read.State == ModelReadState.Read ? read.ChunkKeys : null);
    }

    private readonly record struct FileOutcome<T>(ModelReadState State, T Value, string? Code = null, string? Message = null,
        DateTimeOffset? ObservedAt = null, IReadOnlyList<string>? KeyNames = null, IReadOnlyList<string>? ChunkKeys = null);

    /// <summary>Opens the file, refuses holes before parsing, and maps every layer 4 and reader failure to its state.</summary>
    private async Task<FileOutcome<T>> ReadFileAsync<T>(uint id, Func<Stream, Task<T>> parse, CancellationToken token)
    {
        var opened = await files.OpenReadAsync(FileRef.Id(id), token).ConfigureAwait(false);
        if (opened.Stream is not { } stream)
            return new(StateOf(opened.State), default!, opened.Code, opened.Message, opened.ObservedAt);
        await using (stream.ConfigureAwait(false))
        {
            if (stream.Holes.Count > 0)
            {
                string[] keys = [.. stream.Holes.Select(hole => hole.KeyName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
                var protectedAll = stream.Holes.Sum(hole => hole.Length) >= stream.Length;
                return new(protectedAll ? ModelReadState.Protected : ModelReadState.Partial, default!,
                    protectedAll ? "MODEL_SOURCE_PROTECTED" : "MODEL_SOURCE_PARTIAL",
                    (protectedAll ? "Every byte" : "Part") + " of the file needs TACT key " + string.Join(", ", keys) + ".", null, keys);
            }
            try
            {
                return new(ModelReadState.Read, await parse(stream).ConfigureAwait(false), ChunkKeys: [.. stream.ChunkKeys]);
            }
            catch (ModelFormatException refused) { return new(ModelReadState.Rejected, default!, refused.Code, refused.Message); }
            catch (FileHoleException hole)
            { return new(ModelReadState.Protected, default!, "MODEL_SOURCE_PROTECTED", hole.Message, null, [hole.Hole.KeyName]); }
            catch (FileReadException failed)
            { return new(StateOf(failed.State), default!, failed.Code, failed.Message, failed.ObservedAt); }
        }
    }

    private readonly record struct ExtraIds(uint? Physics, uint[] Recursive, uint[] Geometry);

    /// <summary>PFID, RPID and GPID payloads, which the model reader lists but doesn't parse.</summary>
    private async Task<ExtraIds> ExtraIdsAsync(Stream stream, M2Model model, CancellationToken token)
    {
        uint? physics = null;
        if (Find(model, "PFID") is { } pf)
        {
            if (pf.ByteLength != 4) throw Error("INVALID", "PFID must contain exactly one physics file ID.");
            physics = (await IdsAsync(stream, pf, token).ConfigureAwait(false))[0];
        }
        return new(physics, await ListAsync("RPID").ConfigureAwait(false), await ListAsync("GPID").ConfigureAwait(false));

        async Task<uint[]> ListAsync(string id)
        {
            if (Find(model, id) is not { } chunk) return [];
            if (chunk.ByteLength % 4 != 0) throw Error("INVALID", $"The {id} dependency chunk has a partial ID.");
            if (chunk.ByteLength / 4 > limits.MaxMetadataEntries)
                throw CountLimit($"The model lists {N(chunk.ByteLength / 4)} {id} entries", limits.MaxMetadataEntries);
            return await IdsAsync(stream, chunk, token).ConfigureAwait(false);
        }
    }

    private static M2Chunk? Find(M2Model model, string id) => model.Chunks.FirstOrDefault(chunk => chunk.Id == id);

    private static async Task<uint[]> IdsAsync(Stream stream, M2Chunk chunk, CancellationToken token)
    {
        var bytes = new byte[chunk.ByteLength];
        stream.Position = chunk.Offset;
        try { await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false); }
        catch (EndOfStreamException) { throw Error("INVALID", $"The {chunk.Id} chunk ended unexpectedly."); }
        var ids = new uint[bytes.Length / 4];
        for (var i = 0; i < ids.Length; i++) ids[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4));
        return ids;
    }

    /// <summary>A declared reference: what to stat (an id, a path, or nothing) and the state when nothing is looked up.</summary>
    private sealed record Declaration(ModelReference Reference, FileRef? Lookup);

    private static List<Declaration> Declared(M2Model model, ExtraIds extra)
    {
        var declared = new List<Declaration>();
        void ById(ModelReferenceKind kind, int ordinal, uint id, ModelReferenceState zero, uint? type = null, ushort? animation = null, ushort? variation = null) =>
            declared.Add(id == 0
                ? new(new(kind, ordinal, 0, null, ModelReferenceResolution.None, zero, null, null, type, animation, variation), null)
                : new(new(kind, ordinal, id, null, ModelReferenceResolution.FileDataId, ModelReferenceState.UnknownFile, null, null, type, animation, variation), FileRef.Id(id)));

        if (model.IsChunked)
        {
            for (var i = 0; i < model.SkinFileDataIds.Count; i++) ById(ModelReferenceKind.Skin, i, model.SkinFileDataIds[i], ModelReferenceState.NoFileDeclared);
            for (var i = 0; i < model.LodSkinFileDataIds.Count; i++) ById(ModelReferenceKind.LodSkin, i, model.LodSkinFileDataIds[i], ModelReferenceState.NoFileDeclared);
        }
        else
        {
            for (var i = 0; i < model.ViewCount; i++)
                declared.Add(new(new(ModelReferenceKind.Skin, i, null, null, ModelReferenceResolution.None, ModelReferenceState.NotDeclared, null,
                    "A legacy model has no SFID; its skin files aren't declared."), null));
        }
        foreach (var texture in model.Textures)
        {
            var zero = texture.Type == 0 ? ModelReferenceState.NoFileDeclared : ModelReferenceState.ResolvedElsewhere;
            if (texture.FileDataId is { } id) { ById(ModelReferenceKind.Texture, texture.Ordinal, id, zero, texture.Type); continue; }
            // A legacy MD20 names its type-0 textures; every other type is bound from display or character data.
            var byName = texture.Type == 0 && !string.IsNullOrWhiteSpace(texture.Filename);
            declared.Add(new(new(ModelReferenceKind.Texture, texture.Ordinal, null, byName ? texture.Filename : null, ModelReferenceResolution.None,
                byName ? ModelReferenceState.UnresolvedByName : zero, null, null, texture.Type), byName ? FileRef.ForPath(texture.Filename) : null));
        }
        if (model.SkeletonFileDataId != 0) ById(ModelReferenceKind.Skeleton, 0, model.SkeletonFileDataId, ModelReferenceState.NoExternalFile);
        else if (Find(model, "SKID") is not null) ById(ModelReferenceKind.Skeleton, 0, 0, ModelReferenceState.NoExternalFile);
        for (var i = 0; i < model.BoneFileDataIds.Count; i++) ById(ModelReferenceKind.BoneFile, i, model.BoneFileDataIds[i], ModelReferenceState.NoExternalFile);
        for (var i = 0; i < model.AnimationFiles.Count; i++)
        {
            var animation = model.AnimationFiles[i];
            ById(ModelReferenceKind.AnimationFile, i, animation.FileDataId, ModelReferenceState.NoExternalFile, animation: animation.AnimationId, variation: animation.Variation);
        }
        if (extra.Physics is { } physics) ById(ModelReferenceKind.PhysicsFile, 0, physics, ModelReferenceState.NoExternalFile);
        for (var i = 0; i < extra.Recursive.Length; i++) ById(ModelReferenceKind.RecursiveParticleModel, i, extra.Recursive[i], ModelReferenceState.NoExternalFile);
        for (var i = 0; i < extra.Geometry.Length; i++) ById(ModelReferenceKind.GeometryParticleModel, i, extra.Geometry[i], ModelReferenceState.NoExternalFile);
        return declared;
    }

    /// <summary>Stats every declaration with a lookup, at most <see cref="MaxConcurrentStats"/> at once, in declaration order.</summary>
    private async Task<IReadOnlyList<ModelReference>> ResolveAsync(List<Declaration> declared, CancellationToken token)
    {
        var resolved = new ModelReference[declared.Count];
        for (var i = 0; i < declared.Count; i++) resolved[i] = declared[i].Reference;
        var pending = Enumerable.Range(0, declared.Count).Where(i => declared[i].Lookup is not null);
        await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentStats, CancellationToken = token },
            async (i, inner) => resolved[i] = await StatAsync(declared[i].Reference, declared[i].Lookup!.Value, inner).ConfigureAwait(false))
            .ConfigureAwait(false);
        return Array.AsReadOnly(resolved);
    }

    private async Task<ModelReference> StatAsync(ModelReference reference, FileRef lookup, CancellationToken token)
    {
        FileStat stat;
        try { stat = await files.StatAsync(lookup, token).ConfigureAwait(false); }
        catch (Exception failed) when (failed is not (OperationCanceledException or ObjectDisposedException))
        { return reference with { State = ModelReferenceState.CheckFailed, Message = failed.Message }; }
        var byName = lookup.Path is not null;
        var state = stat.State switch
        {
            FileLookupState.Found => ModelReferenceState.Found,
            FileLookupState.NotInEncoding => ModelReferenceState.NotInEncoding,
            FileLookupState.NotInLocale => ModelReferenceState.NotInLocale,
            _ => byName ? ModelReferenceState.UnresolvedByName : ModelReferenceState.UnknownFile,
        };
        var resolution = !byName ? ModelReferenceResolution.FileDataId : stat.File?.PathSource switch
        {
            FilePathSource.RootHash => ModelReferenceResolution.RootName,
            FilePathSource.Listfile => ModelReferenceResolution.ListfileName,
            _ => ModelReferenceResolution.None,
        };
        return reference with { State = state, Resolution = resolution, File = stat.File, FileDataId = reference.FileDataId ?? stat.File?.FileDataId };
    }

    private static ModelReadState StateOf(FileLookupState state) => state switch
    {
        FileLookupState.NotInLocale => ModelReadState.NotInLocale,
        FileLookupState.NotInEncoding => ModelReadState.NotInEncoding,
        _ => ModelReadState.UnknownFile,
    };

    // A failure that claims bytes or a state this code doesn't know is Corrupt: never Read, never a throw.
    private static ModelReadState StateOf(FileReadState state) => state switch
    {
        FileReadState.Protected => ModelReadState.Protected,
        FileReadState.Partial => ModelReadState.Partial,
        FileReadState.UnknownFile => ModelReadState.UnknownFile,
        FileReadState.NotInLocale => ModelReadState.NotInLocale,
        FileReadState.NotInEncoding => ModelReadState.NotInEncoding,
        FileReadState.Missing => ModelReadState.Missing,
        FileReadState.NeedsNetwork => ModelReadState.NeedsNetwork,
        FileReadState.Unavailable => ModelReadState.Unavailable,
        FileReadState.Corrupt => ModelReadState.Corrupt,
        FileReadState.Unsupported => ModelReadState.Unsupported,
        FileReadState.WrongKey => ModelReadState.WrongKey,
        FileReadState.StoreFailed => ModelReadState.StoreFailed,
        FileReadState.TooLarge => ModelReadState.TooLarge,
        _ => ModelReadState.Corrupt,
    };
}
