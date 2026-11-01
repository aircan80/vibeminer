using VibeMiner.Builds;
using VibeMiner.Files;
using VibeMiner.Formats;
using VibeMiner.Formats.Images;
using VibeMiner.Formats.Models;
using VibeMiner.Formats.Tables;

namespace VibeMiner.Concepts.Creatures;

/// <summary>
/// Layer 6: a build's creatures per build and locale (D462): the Creature table's sixteen named fields, each slot's display
/// chain CreatureDisplayInfo -> CreatureModelData -> model file, and the model read through layer 5 in a detail. Implementations:
/// <see cref="CreaturesService"/> over layer 5, and the in-memory fake in <c>VibeMiner.Concepts.Testing</c>; one contract suite
/// runs against both. Table, column and file problems never throw: they come back as named states. Calls throw only for bad
/// arguments (<see cref="ArgumentException"/>), cancellation, a disposed handle (<see cref="ObjectDisposedException"/>) and what
/// layer 5 throws (<see cref="TablesBusyException"/>).
/// </summary>
public interface ICreatures
{
    /// <summary>Opens the build's tables for one locale (a <see cref="FileLocales"/> name). Opening reads no table.</summary>
    Task<CreaturesOpenResult> ForAsync(BuildRef build, string locale, CancellationToken cancellationToken);
}

/// <summary>The outcome of <see cref="ICreatures.ForAsync"/>. <see cref="Creatures"/> is set only when <see cref="State"/> is Open; a failure keeps layer 4's state, code and time.</summary>
public sealed record CreaturesOpenResult(BuildRef Build, string Locale, FilesOpenState State, IBuildCreatures? Creatures, string? Code, string? Message,
    DateTimeOffset? ObservedAt);

/// <summary>One build and locale's creatures. Calls may run concurrently. Dispose the handle when done: every later call throws <see cref="ObjectDisposedException"/>.</summary>
public interface IBuildCreatures : IAsyncDisposable
{
    BuildRef Build { get; }
    string Locale { get; }

    /// <summary>One page of creatures. See <see cref="CreatureQuery"/> and <see cref="CreaturesPage"/>. Reads no display table and no model.</summary>
    Task<CreaturesPage> ListAsync(CreatureQuery query, TablePriority priority, CancellationToken cancellationToken);

    /// <summary>One creature with its row, its four display chains and each resolved model's read (here, never in a list).</summary>
    Task<CreatureResult> GetAsync(uint id, TablePriority priority, CancellationToken cancellationToken);
}

/// <summary>
/// A list request, under the same rules as items (<see cref="CreatureQueries.Validate"/>): <see cref="Query"/> is a literal
/// substring of <c>Name_lang</c> (ordinal, ignore case; empty lists every creature), at most 128 Unicode scalars and 512 UTF-8
/// bytes, no NUL. <see cref="ExactId"/> lists only that creature and can't be combined with a non-empty query. <see cref="Limit"/>
/// is 1 to 50. <see cref="After"/> is the <see cref="CreaturesPage.Next"/> of the previous page of the same request.
/// </summary>
public sealed record CreatureQuery(string Query = "", uint? ExactId = null, int Limit = CreatureQueries.MaxLimit, string? After = null);

public enum CreaturesPageState
{
    Ok,
    /// <summary>The Creature table isn't readable (not Ready or Partial); <see cref="CreaturesPage.Creature"/> says why.</summary>
    CreatureNotReadable,
    /// <summary>CREATURES_SCHEMA_UNSUPPORTED: the definition and header don't give the sixteen fields by name, kind, width and sign.</summary>
    SchemaUnsupported,
    /// <summary>CREATURES_PROJECTION_INVALID: a read cell doesn't fit its field (not canonical, out of its width, a non-finite probability).</summary>
    SourceInvalid,
    /// <summary>The Creature table's identity changed since the cursor was made; the page is never re-based.</summary>
    Stale,
    /// <summary>INVALID_CURSOR: the cursor belongs to another request, or points past the table.</summary>
    InvalidCursor,
}

/// <summary>
/// A page's outcome. Only Ok carries entries, in the Creature table's layer 5 row order (D451, D462). <see cref="Next"/> continues
/// the same request, null only at the end; a page stops after its limit or its row scan budget, so it can have fewer entries (or
/// none) and still continue. <see cref="Scanned"/> is the rows looked at. For an exact ID, <see cref="Exact"/> is the lookup's
/// outcome: an Unknown or Protected ID (<see cref="ProtectedKey"/> names the key) lists nothing but is never Absent.
/// </summary>
public sealed record CreaturesPage(CreaturesPageState State, IReadOnlyList<CreatureSummary> Entries, string? Next, long Scanned, TableEntry? Creature,
    TableRowOutcome? Exact = null, string? ProtectedKey = null, string? Code = null, string? Message = null);

public enum CreatureNameState
{
    Valid,
    /// <summary>Name_lang has length 0.</summary>
    Empty,
    /// <summary>Name_lang has a text issue (DB2_TEXT_ENCODING_INVALID); <see cref="CreatureName.Issue"/> names it.</summary>
    Invalid,
}

/// <summary>A creature's name from <c>Name_lang</c> only. <see cref="Value"/> is set for Valid (whitespace is Valid) and "" for Empty, null for Invalid.</summary>
public sealed record CreatureName(CreatureNameState State, string? Value, string? Issue = null);

/// <summary>One field's source cell: the header column, its index and its exact value.</summary>
public sealed record CreatureSourceValue(string Column, int ColumnIndex, string Value);

public enum CreatureSlotState
{
    /// <summary>DisplayID is above 0.</summary>
    Declared,
    /// <summary>DisplayID is 0.</summary>
    NoDeclaredDisplay,
    /// <summary>DisplayID is below 0: never a reference.</summary>
    InvalidDisplayReference,
}

/// <summary>A declared display slot (0 to 3): <c>DisplayID[slot]</c> and <c>DisplayProbability[slot]</c>, the float32 exactly as read (D107).</summary>
public sealed record CreatureDisplaySlot(int Slot, int DisplayId, float Probability, CreatureSlotState State);

/// <summary>
/// One listed creature. <see cref="Facts"/> are Classification, CreatureType, CreatureFamily and StartAnimState, in that
/// order. <see cref="Slots"/> are the four declared display slots.
/// </summary>
public sealed record CreatureSummary(uint Id, bool IsCopy, uint? CopySourceId, CreatureName Name, IReadOnlyList<CreatureSourceValue> Facts,
    IReadOnlyList<CreatureDisplaySlot> Slots);

public enum CreatureResultState
{
    Found,
    /// <summary>No Creature row has the ID, and the table is complete (Ready).</summary>
    Absent,
    /// <summary>Not in the readable sections of a Partial Creature table. Never proof of absence.</summary>
    Unknown,
    /// <summary>The ID is in a keyed section whose key isn't known; <see cref="CreatureResult.ProtectedKey"/> names it.</summary>
    Protected,
    CreatureNotReadable,
    SchemaUnsupported,
    SourceInvalid,
}

/// <summary>A creature by ID. <see cref="Detail"/> is set only for Found.</summary>
public sealed record CreatureResult(CreatureResultState State, CreatureDetail? Detail, TableEntry? Creature, string? ProtectedKey = null,
    string? Code = null, string? Message = null);

/// <summary>
/// One creature's detail: the summary, its row exactly as layer 5 read it, the reviewed label of its Classification (0 to 6:
/// Normal, Elite, Rare elite, World boss, Rare, Trivial, Minus; null for any other value), and one display chain per slot.
/// </summary>
public sealed record CreatureDetail(CreatureSummary Summary, TableRow Row, string? ClassificationLabel, IReadOnlyList<CreatureDisplayChain> Displays);

/// <summary>Where a slot's display chain ended (D463). Display* states are CreatureDisplayInfo's, Model* states CreatureModelData's.</summary>
public enum CreatureDisplayChainState
{
    /// <summary>The slot's DisplayID is 0; nothing is looked up.</summary>
    NoDeclaredDisplay,
    /// <summary>The slot's DisplayID is negative; nothing is looked up.</summary>
    InvalidDisplayReference,
    /// <summary>CreatureDisplayInfo isn't Ready or Partial; <see cref="CreatureDisplayChain.Display"/> says why.</summary>
    DisplayNotReadable,
    /// <summary>CREATURE_DISPLAY_SCHEMA_UNSUPPORTED: the layout isn't a reviewed one, or ModelID isn't one verified u16 scalar.</summary>
    DisplayUnsupported,
    /// <summary>No CreatureDisplayInfo row has the DisplayID, and the table is complete.</summary>
    DisplayAbsent,
    /// <summary>The DisplayID is outside a Partial CreatureDisplayInfo's readable sections. Never proof of absence.</summary>
    DisplayUnknown,
    /// <summary>The DisplayID is in a keyed section whose key isn't known; <see cref="CreatureDisplayChain.ProtectedKey"/> names it.</summary>
    DisplayProtected,
    /// <summary>CREATURE_DISPLAY_INTEGRITY: the row's ModelID isn't a canonical unsigned value of at most 65535.</summary>
    DisplayInvalid,
    /// <summary>ModelID is 0: the display declares no model.</summary>
    NoDeclaredModel,
    ModelNotReadable,
    /// <summary>CREATURE_DISPLAY_SCHEMA_UNSUPPORTED: the layout isn't a reviewed one, or FileDataID isn't one verified u32 scalar.</summary>
    ModelUnsupported,
    ModelAbsent,
    ModelUnknown,
    ModelProtected,
    /// <summary>CREATURE_DISPLAY_INTEGRITY: the row's FileDataID isn't a canonical unsigned 32-bit value.</summary>
    ModelInvalid,
    /// <summary>FileDataID is 0: the model data declares no file.</summary>
    NoDeclaredModelFile,
    /// <summary>The chain reached a model FileDataID; <see cref="CreatureDisplayChain.Model"/> is its read.</summary>
    MetadataResolved,
}

/// <summary>
/// One slot's display chain. <see cref="Display"/> and <see cref="ModelData"/> are the tables' checked entries (null when the
/// chain didn't reach them); the rows are as layer 5 read them. <see cref="ModelId"/> is set once a display row gave a ModelID,
/// <see cref="ModelFileDataId"/> once a model row gave a FileDataID (0 included). Facts and texture evidence come from the
/// rows found; <see cref="Model"/> is set only for MetadataResolved.
/// </summary>
public sealed record CreatureDisplayChain(CreatureDisplaySlot Slot, CreatureDisplayChainState State, TableEntry? Display = null, TableRow? DisplayRow = null,
    uint? ModelId = null, TableEntry? ModelData = null, TableRow? ModelRow = null, uint? ModelFileDataId = null, string? ProtectedKey = null,
    string? Code = null, string? Message = null, CreatureRecordFacts? DisplayFacts = null, CreatureRecordFacts? ModelFacts = null,
    CreatureTextureEvidence? Textures = null, CreatureModel? Model = null);

public enum CreatureFactsState
{
    Ready,
    /// <summary>The row has a text issue or doesn't line up with its header: its values weren't kept whole, so it has no facts.</summary>
    RowNotWhole,
    /// <summary>CREATURES_RESULT_LIMIT: more than 512 columns, or a fact wider than 16 cells. Refused whole, never cut.</summary>
    LimitExceeded,
}

public enum CreatureFactState
{
    Value,
    /// <summary>The table's layout has no column of the fact's field: unsupported, never zero.</summary>
    Unsupported,
}

/// <summary>A cell of a display or model row: header column name, index and exact value.</summary>
public sealed record CreatureFactCell(string Column, int ColumnIndex, string Value);

/// <summary>A reviewed fact (<c>model</c>, <c>scale</c>, <c>textureVariations</c>; <c>file</c>, <c>scale</c>), picked by base field name.</summary>
public sealed record CreatureFact(string Key, CreatureFactState State, IReadOnlyList<CreatureFactCell> Cells);

/// <summary>
/// A display or model row's facts (creature-display-facts-v1, D107): the reviewed facts, then every other non-ID column as
/// <see cref="Raw"/>. Nothing is calculated, combined or defaulted.
/// </summary>
public sealed record CreatureRecordFacts(CreatureFactsState State, IReadOnlyList<CreatureFact> Facts, IReadOnlyList<CreatureFactCell> Raw, string? Code = null);

/// <summary>
/// The display row's texture fields (D088): <c>ExtendedDisplayInfoID</c> and all four <c>TextureVariationFileDataID</c>
/// positions, zeros and position 3 kept. Null on the chain when a field is missing, not one plain integer column, has a text
/// issue or isn't a canonical int32; nothing is defaulted.
/// </summary>
public sealed record CreatureTextureEvidence(int ExtendedDisplayInfoId, IReadOnlyList<int> TextureVariations);

public enum CreatureModelState
{
    /// <summary>The model was read; see <see cref="CreatureModel.Textures"/> and <see cref="CreatureModel.Appearance"/>.</summary>
    Read,
    /// <summary>The build's files didn't open; <see cref="CreatureModel.Code"/> is layer 4's.</summary>
    FilesNotOpen,
    /// <summary>Layer 5 didn't read the model; <see cref="CreatureModel.ReadState"/> says why (UnknownFile, Protected with keys, Rejected...).</summary>
    ReadFailed,
}

public enum CreatureTextureSource
{
    /// <summary>Type 0: the model's own TXID entry (or a legacy embedded name).</summary>
    Txid,
    /// <summary>Types 11, 12, 13: <c>TextureVariationFileDataID[0/1/2]</c> of the display row.</summary>
    Variation,
    /// <summary>Any other type: resolved by character, item or emblem data, never by the creature display.</summary>
    Other,
}

public enum CreatureTextureState
{
    /// <summary>A FileDataID above 0.</summary>
    Resolved,
    /// <summary>The TXID entry or display variation is 0 (or the evidence is unreadable): nothing is substituted.</summary>
    Unresolved,
    /// <summary>Not a creature display texture (<see cref="CreatureTextureSource.Other"/>); never counted unresolved.</summary>
    ResolvedElsewhere,
}

/// <summary>
/// One texture the model declares, every one kept as data. <see cref="Drawn"/> is true when an opaque or alpha-key batch of the
/// main skin uses it; only drawn textures count toward the appearance (D088, D464). <see cref="Reference"/> is layer 5's
/// reference for the ordinal, when it has one. <see cref="Image"/> and the fields after it are the BLP header inspection, set
/// only for a drawn texture when the appearance got as far as reading textures.
/// </summary>
public sealed record CreatureTexture(int Ordinal, uint Type, uint Flags, string Filename, CreatureTextureSource Source, int? Variation, uint? FileDataId,
    CreatureTextureState State, bool Drawn = false, ModelReference? Reference = null, BlpImageState? Image = null, FileReadState? FileState = null,
    IReadOnlyList<string>? HoleKeys = null, string? Code = null);

/// <summary>Whether the model's textures resolve for a creature draw (D088, D464), in the order the rules are checked.</summary>
public enum CreatureAppearanceState
{
    /// <summary>Every creature texture resolved and its BLP header reads.</summary>
    Ready,
    /// <summary>The model wasn't read.</summary>
    ModelNotRead,
    /// <summary>MODEL_UNSUPPORTED: the model is outside geometry's profile (not a chunked 272 or 274, or an external skeleton or bone file). Checked first.</summary>
    GeometryUnsupported,
    /// <summary>MODEL_UNSUPPORTED: the model declares no positive main skin (<c>SkinFileDataIds[0]</c>). Checked next, as geometry was.</summary>
    NoMainSkin,
    /// <summary>The main skin wasn't read; <see cref="CreatureModel.SkinState"/> and <see cref="CreatureModel.SkinKeyNames"/> say why.</summary>
    SkinNotRead,
    /// <summary>CREATURE_DISPLAY_TEXTURES_UNREADABLE (no texture evidence) or CREATURE_APPEARANCE_TEXTURE_UNRESOLVED (a drawn texture with a zero FileDataID at <see cref="CreatureAppearance.TextureOrdinal"/>).</summary>
    TextureUnresolved,
    /// <summary>CREATURE_APPEARANCE_CHARACTER_MODEL: ExtendedDisplayInfoID isn't 0; its baked textures aren't resolved.</summary>
    CharacterModel,
    /// <summary>CREATURE_APPEARANCE_BLEND_UNSUPPORTED: the batch at <see cref="CreatureAppearance.BatchOrdinal"/> blends with a mode above 7, or every batch with surface triangles blends with mode 2-7 (the first is named); nothing opaque is left to draw.</summary>
    BlendUnsupported,
    /// <summary>CREATURE_APPEARANCE_MATERIAL_UNSUPPORTED: no batch draws any surface triangle, or the batch at <see cref="CreatureAppearance.BatchOrdinal"/> names a section, material or texture lookup the model doesn't declare, or a drawn texture with flags outside the U/V wrap bits or of a type with no creature binding (<see cref="CreatureAppearance.TextureOrdinal"/>).</summary>
    MaterialUnsupported,
    /// <summary>The texture at <see cref="CreatureAppearance.TextureOrdinal"/> lies in holes; its texture names the keys.</summary>
    TextureProtected,
    TextureUnsupported,
    /// <summary>Over the decoder limits, or no declared mip.</summary>
    TextureLimit,
    TextureCorrupt,
    /// <summary>Layer 4 didn't give the texture's bytes; its texture's FileState says why.</summary>
    TextureUnavailable,
}

/// <summary>
/// A model's appearance outcome. <see cref="TextureOrdinal"/> names the first texture that stopped it, <see cref="BatchOrdinal"/>
/// the batch. <see cref="BlendedBatches"/> is how many main skin batches blend with mode 2-7 (counted, not drawn, D094), set
/// once the skin's batches were read.
/// </summary>
public sealed record CreatureAppearance(CreatureAppearanceState State, string? Code = null, string? Message = null, int? TextureOrdinal = null,
    int? BatchOrdinal = null, int? BlendedBatches = null);

/// <summary>
/// The model a chain resolved, read with layer 5's <see cref="BuildModels.ReadModelAsync"/>. <see cref="MainSkinFileDataId"/>
/// is <c>SkinFileDataIds[0]</c> when above 0; <see cref="SkinState"/> is how reading it (<see cref="BuildModels.ReadSkinAsync"/>,
/// profile 0) ended, null when it wasn't asked for. Codes, messages, times and keys are the layer's that said so.
/// </summary>
public sealed record CreatureModel(CreatureModelState State, uint FileDataId, ModelReadState? ReadState, CreatureAppearance Appearance,
    IReadOnlyList<CreatureTexture> Textures, uint? MainSkinFileDataId = null, string? Code = null, string? Message = null,
    DateTimeOffset? ObservedAt = null, IReadOnlyList<string>? KeyNames = null, ModelReadState? SkinState = null,
    IReadOnlyList<string>? SkinKeyNames = null);
