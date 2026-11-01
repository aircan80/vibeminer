using VibeMiner.Concepts.Tables;
using VibeMiner.Formats;
using VibeMiner.Formats.Definitions;
using VibeMiner.Formats.Models;
using VibeMiner.Formats.Tables;

namespace VibeMiner.Concepts.Items;

/// <summary>How one lookup of a row by ID went (D467). Every state but Found leaves the row and what hangs off it unset.</summary>
public enum ItemRefState
{
    Found,
    /// <summary>No row has the ID, and the table is Ready. For a Component* table this is "no component key".</summary>
    Absent,
    /// <summary>Not in the readable sections of a Partial table. Never proof of absence.</summary>
    Unknown,
    /// <summary>The ID is in a keyed section whose key isn't known; the ProtectedKey names it.</summary>
    Protected,
    /// <summary>The table isn't Ready or Partial; its entry keeps the layer 5 state.</summary>
    TableNotReadable,
    /// <summary>ITEM_APPEARANCE_PROFILE_UNSUPPORTED: the layout lacks one plain integer column a field needs, of at most 32 bits (D101).</summary>
    Unsupported,
    /// <summary>ITEM_APPEARANCE_VALUE_INVALID: the key isn't a canonical integer in 0..uint32.</summary>
    InvalidValue,
}

/// <summary>How the ItemModifiedAppearance scan by ItemID went (D467).</summary>
public enum ItemAppearancesState
{
    /// <summary>ItemModifiedAppearance is Ready: <see cref="ItemAppearanceSet.Rows"/> are every row of the item.</summary>
    Complete,
    /// <summary>ItemModifiedAppearance is Partial: the rows are its readable ones and more may be hidden.</summary>
    Incomplete,
    /// <summary>ITEM_APPEARANCE_ROW_LIMIT: more rows than <see cref="ItemsOptions.MaxAppearanceRows"/> matched; the first ones are kept.</summary>
    RowLimit,
    /// <summary>RELATION_INDEX_LIMIT: the handle's relation indexes are full (D471), so the rows weren't looked up. Not "none".</summary>
    IndexLimit,
    /// <summary>The table's identity changed while it was indexed or read, and again after one rebuild.</summary>
    Stale,
    TableNotReadable,
    /// <summary>ITEM_APPEARANCE_PROFILE_UNSUPPORTED, or the index's RELATION_INDEX_COLUMN.</summary>
    Unsupported,
}

/// <summary>Which appearance is the item's default (D467). Never picked silently.</summary>
public enum ItemDefaultAppearanceState
{
    /// <summary>Exactly one row has ItemAppearanceModifierID 0 and the rows are Complete; <see cref="ItemAppearanceSet.DefaultIndex"/> points at it.</summary>
    Default,
    /// <summary>More than one row has ItemAppearanceModifierID 0.</summary>
    Ambiguous,
    /// <summary>The rows are Complete and none has ItemAppearanceModifierID 0.</summary>
    None,
    /// <summary>The rows aren't Complete and at most one such row was seen: no proof either way.</summary>
    Unknown,
}

/// <summary>
/// An item's appearances (D467-D470), in the detail only. <see cref="Rows"/> are the ItemModifiedAppearance rows with this
/// ItemID in layer 5's row order, each resolved down to its model and material files. <see cref="Matches"/> is how many rows
/// have the ItemID (more than <see cref="Rows"/> for RowLimit). <see cref="Models"/> are the distinct model files of every row, read with layer 5's <see cref="BuildModels"/>.
/// </summary>
public sealed record ItemAppearanceSet(ItemAppearancesState State, IReadOnlyList<ItemAppearanceEntry> Rows, ItemDefaultAppearanceState Default,
    int? DefaultIndex, int Matches, TableEntry? Table, ItemModelRefs Models, string? Code = null, string? Message = null);

/// <summary>
/// One ItemModifiedAppearance row. The three values are its cells when each is a canonical integer, else null (a null modifier
/// never counts as 0). <see cref="Appearance"/> is the ItemAppearance row it points at.
/// </summary>
public sealed record ItemAppearanceEntry(TableRow Row, long? ItemAppearanceModifierId, long? OrderIndex, long? ItemAppearanceId, ItemAppearanceRef Appearance);

/// <summary>An ItemAppearance row by ID: its icon and display IDs when Found (null when a cell isn't a canonical uint32), and the display it points at.</summary>
public sealed record ItemAppearanceRef(ItemRefState State, uint? Id, TableRow? Row, uint? DefaultIconFileDataId, uint? ItemDisplayInfoId,
    ItemDisplayRef? Display, TableEntry? Table, string? ProtectedKey = null, string? Code = null, string? Message = null);

/// <summary>An ItemDisplayInfo row by ID. <see cref="Slots"/> are slots 0 and 1 when Found, kept apart, never merged.</summary>
public sealed record ItemDisplayRef(ItemRefState State, uint? Id, TableRow? Row, IReadOnlyList<ItemDisplaySlot> Slots, TableEntry? Table,
    string? ProtectedKey = null, string? Code = null, string? Message = null);

/// <summary>One ItemDisplayInfo slot: <c>ModelResourcesID[n]</c> and <c>ModelMaterialResourcesID[n]</c>.</summary>
public sealed record ItemDisplaySlot(int Slot, ItemResourceRef Model, ItemResourceRef Material);

public enum ItemResourceKind
{
    /// <summary>ModelFileData rows by ModelResourcesID; the file is the row's FileDataID (its key); component keys from ComponentModelFileData.</summary>
    Model,
    /// <summary>TextureFileData rows by MaterialResourcesID; the file is the row's FileDataID (its key); component keys from ComponentTextureFileData.</summary>
    Material,
}

/// <summary>How a resource ID resolved to files (D468).</summary>
public enum ItemResourceState
{
    /// <summary>The slot's resource ID is 0: nothing in that slot.</summary>
    NoResource,
    /// <summary>At least one row has the ID. <see cref="ItemResourceRef.Complete"/> says whether the table is Ready (all of them).</summary>
    Found,
    /// <summary>Unresolved: the ID is nonzero, the table is Ready, and no row has it.</summary>
    Absent,
    /// <summary>
    /// Unresolved: no readable row of a Partial table has it. Never proof of absence. A relation can't say which hidden rows hold
    /// the ID, so hidden keyed sections never make this Protected; <see cref="ItemResourceRef.KeyNames"/> lists their keys as context.
    /// </summary>
    Unknown,
    /// <summary>ITEM_APPEARANCE_VALUE_INVALID: the slot's cell isn't a canonical integer in 0..uint32.</summary>
    InvalidValue,
    /// <summary>ITEM_APPEARANCE_ROW_LIMIT: more rows than <see cref="ItemsOptions.MaxResourceFiles"/> have the ID; the first ones are kept.</summary>
    RowLimit,
    /// <summary>RELATION_INDEX_LIMIT: the handle's relation indexes are full (D471). Not Absent.</summary>
    IndexLimit,
    Stale,
    TableNotReadable,
    Unsupported,
}

/// <summary>
/// One slot's model or material resource. <see cref="ResourceId"/> is the slot's cell when it is a canonical integer.
/// <see cref="Files"/> are the matching rows in layer 5's order, <see cref="Matches"/> how many there are. <see cref="Complete"/>
/// is true only when the lookup answered on a Ready table. <see cref="KeyNames"/> are a Partial table's hidden keys, as context.
/// </summary>
public sealed record ItemResourceRef(ItemResourceKind Kind, int Slot, long? ResourceId, ItemResourceState State, bool Complete,
    IReadOnlyList<ItemResourceFile> Files, int Matches, TableEntry? Table, IReadOnlyList<string>? KeyNames = null, string? Code = null,
    string? Message = null);

/// <summary>One file of a resource: the row's FileDataID (its key) and the file's Component* row.</summary>
public sealed record ItemResourceFile(uint FileDataId, TableRow Row, ItemComponentKey Component);

/// <summary>
/// A file's race, gender, class and position from ComponentModelFileData or ComponentTextureFileData, keyed by the file's
/// FileDataID. Absent is "no component key" (Ready table, no row). Each value is its cell when the layout has the field and it
/// is a canonical integer; ComponentTextureFileData has no PositionIndex.
/// </summary>
public sealed record ItemComponentKey(ItemRefState State, long? RaceId = null, long? GenderIndex = null, long? ClassId = null, long? PositionIndex = null,
    TableRow? Row = null, TableEntry? Table = null, string? ProtectedKey = null, string? Code = null, string? Message = null);

/// <summary>An icon with the field it came from (D469): <c>Item.IconFileDataID</c> of the item, or <c>ItemAppearance.DefaultIconFileDataID</c> of row <see cref="RowId"/>.</summary>
public sealed record ItemIconSourced(string Table, string Column, uint RowId, ItemIcon Icon);

public enum ItemModelsState
{
    /// <summary>Every distinct model file was read (or there are none).</summary>
    Complete,
    /// <summary>ITEM_MODEL_CAP: more than <see cref="ItemsOptions.MaxModels"/> distinct files; the rest are listed as NotRead.</summary>
    Capped,
}

public enum ItemModelRefState
{
    /// <summary><see cref="BuildModels.ReadModelAsync"/> ran; <see cref="ItemModelRef.Read"/> is its state.</summary>
    Read,
    /// <summary>Past the cap: listed, not read.</summary>
    NotRead,
    /// <summary>The build's files didn't open; the code is layer 4's.</summary>
    FilesNotOpen,
}

/// <summary>An item's distinct model files, in the order the appearance rows and slots first name them (D470).</summary>
public sealed record ItemModelRefs(ItemModelsState State, IReadOnlyList<ItemModelRef> Models, string? Code = null, string? Message = null)
{
    public static ItemModelRefs None { get; } = new(ItemModelsState.Complete, []);
}

/// <summary>
/// One model file. For Read, <see cref="Read"/> is layer 5's <see cref="ModelReadState"/> with its code, message, time and key
/// names; when that is Read, <see cref="Textures"/> are the model's texture references and <see cref="SkinFileDataIds"/> its
/// SFID count of main skins.
/// </summary>
public sealed record ItemModelRef(uint FileDataId, ItemModelRefState State, ModelReadState? Read = null, IReadOnlyList<ItemModelTexture>? Textures = null,
    int SkinFileDataIds = 0, string? Code = null, string? Message = null, DateTimeOffset? ObservedAt = null, IReadOnlyList<string>? KeyNames = null);

/// <summary>A texture reference of a read model: its ordinal, M2 type, declared FileDataID and layer 5's reference state.</summary>
public sealed record ItemModelTexture(int Ordinal, uint? Type, uint? FileDataId, ModelReferenceState State);

/// <summary>
/// The appearance chain on layer 5 (D467, D468): ItemModifiedAppearance by ItemID (filtered scan), ItemAppearance by ID,
/// ItemDisplayInfo by ID, ModelFileData by ModelResourcesID and TextureFileData by MaterialResourcesID (through the handle's
/// <see cref="RelationIndexes"/>, D471), and Component* by file ID. One reader per detail; it remembers each table's schema and each lookup it made.
/// </summary>
internal sealed class ItemAppearanceReader(IBuildTables tables, RelationIndexes relations, ItemsOptions options, TablePriority priority,
    CancellationToken cancellationToken)
{
    public const string ProfileUnsupportedCode = "ITEM_APPEARANCE_PROFILE_UNSUPPORTED";
    public const string ValueInvalidCode = "ITEM_APPEARANCE_VALUE_INVALID";
    public const string RowLimitCode = "ITEM_APPEARANCE_ROW_LIMIT";
    public const string ModifiedTable = "ItemModifiedAppearance", AppearanceTable = "ItemAppearance", DisplayTable = "ItemDisplayInfo";
    public const string ModelTable = "ModelFileData", TextureTable = "TextureFileData";
    public const string ComponentModelTable = "ComponentModelFileData", ComponentTextureTable = "ComponentTextureFileData";
    public const string IconColumn = "DefaultIconFileDataID";

    private readonly Dictionary<string, TableSchemaResult> schemas = [];
    private readonly Dictionary<uint, ItemAppearanceRef> appearances = [];
    private readonly Dictionary<uint, ItemDisplayRef> displays = [];
    private readonly Dictionary<(ItemResourceKind, long), ItemResourceRef> resources = [];
    private readonly Dictionary<(ItemResourceKind, uint), ItemComponentKey> components = [];


    public async Task<ItemAppearanceSet> ReadAsync(uint itemId)
    {
        var (entry, columns, schema) = await SchemaAsync(ModifiedTable).ConfigureAwait(false);
        if (columns is null) return Set(ItemAppearancesState.TableNotReadable, entry);
        var fields = new[] { "ItemID", "ItemAppearanceModifierID", "OrderIndex", "ItemAppearanceID" }.Select(name => Field(columns, schema, name)).ToArray();
        if (fields.Any(index => index < 0))
            return Set(ItemAppearancesState.Unsupported, entry, ProfileUnsupportedCode,
                "ItemModifiedAppearance has no one plain integer ItemID, ItemAppearanceModifierID, OrderIndex and ItemAppearanceID of at most 32 bits.");
        var scan = await relations.LookupAsync(ModifiedTable, "ItemID", itemId, options.MaxAppearanceRows, priority, cancellationToken).ConfigureAwait(false);
        var state = scan.State switch
        {
            RelationLookupState.Ok when scan.Matches > options.MaxAppearanceRows => ItemAppearancesState.RowLimit,
            RelationLookupState.Ok => scan.Complete ? ItemAppearancesState.Complete : ItemAppearancesState.Incomplete,
            RelationLookupState.IndexLimit => ItemAppearancesState.IndexLimit,
            RelationLookupState.Stale => ItemAppearancesState.Stale,
            RelationLookupState.NotReadable => ItemAppearancesState.TableNotReadable,
            _ => ItemAppearancesState.Unsupported,
        };
        if (state is not (ItemAppearancesState.Complete or ItemAppearancesState.Incomplete or ItemAppearancesState.RowLimit))
            return Set(state, scan.Table ?? entry, scan.Code, scan.Message);
        var rows = new List<ItemAppearanceEntry>(scan.Rows.Count);
        foreach (var row in scan.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var modifier = Integer(row.Row, fields[1]);
            var order = Integer(row.Row, fields[2]);
            var appearanceId = Integer(row.Row, fields[3]);
            var appearance = appearanceId is long target and >= 0 and <= uint.MaxValue ? await AppearanceAsync((uint)target).ConfigureAwait(false)
                : new ItemAppearanceRef(ItemRefState.InvalidValue, null, null, null, null, null, null, null, ValueInvalidCode,
                    "ItemModifiedAppearance.ItemAppearanceID is not a canonical integer in 0..uint32.");
            rows.Add(new(row, modifier, order, appearanceId, appearance));
        }
        var defaults = rows.Select((row, index) => (row, index)).Where(pair => pair.row.ItemAppearanceModifierId == 0).ToArray();
        var complete = state == ItemAppearancesState.Complete;
        var (kind, at) = defaults.Length > 1 ? (ItemDefaultAppearanceState.Ambiguous, (int?)null)
            : !complete ? (ItemDefaultAppearanceState.Unknown, null)
            : defaults.Length == 1 ? (ItemDefaultAppearanceState.Default, defaults[0].index) : (ItemDefaultAppearanceState.None, null);
        var (code, message) = state == ItemAppearancesState.RowLimit
            ? (RowLimitCode, $"{scan.Matches} ItemModifiedAppearance rows have ItemID {itemId}; a detail keeps {options.MaxAppearanceRows}.")
            : ((string?)null, (string?)null);
        return new(state, rows, kind, at, scan.Matches, scan.Table, ItemModelRefs.None, code, message);
    }

    private static ItemAppearanceSet Set(ItemAppearancesState state, TableEntry? table, string? code = null, string? message = null) =>
        new(state, [], ItemDefaultAppearanceState.Unknown, null, 0, table, ItemModelRefs.None, code, message);

    private async Task<ItemAppearanceRef> AppearanceAsync(uint id)
    {
        if (appearances.TryGetValue(id, out var known)) return known;
        var (entry, columns, schema) = await SchemaAsync(AppearanceTable).ConfigureAwait(false);
        ItemAppearanceRef result;
        if (columns is null) result = new(ItemRefState.TableNotReadable, id, null, null, null, null, entry);
        else
        {
            var icon = Field(columns, schema, IconColumn);
            var display = Field(columns, schema, "ItemDisplayInfoID");
            if (icon < 0 || display < 0)
                result = new(ItemRefState.Unsupported, id, null, null, null, null, entry, null, ProfileUnsupportedCode,
                    "ItemAppearance has no one plain integer DefaultIconFileDataID and ItemDisplayInfoID of at most 32 bits.");
            else
            {
                var found = await tables.GetAsync(TableRef.ForName(AppearanceTable), id, priority, cancellationToken).ConfigureAwait(false);
                if (found.Outcome != TableRowOutcome.Found) result = new(RefState(found.Outcome), id, null, null, null, null, found.Table, found.ProtectedKey);
                else
                {
                    var iconId = Uint(found.Row!.Row, icon);
                    var displayId = Uint(found.Row.Row, display);
                    var displayRef = displayId is { } did ? await DisplayAsync(did).ConfigureAwait(false)
                        : new ItemDisplayRef(ItemRefState.InvalidValue, null, null, [], null, null, ValueInvalidCode,
                            "ItemAppearance.ItemDisplayInfoID is not a canonical integer in 0..uint32.");
                    result = new(ItemRefState.Found, id, found.Row, iconId, displayId, displayRef, found.Table);
                }
            }
        }
        appearances[id] = result;
        return result;
    }

    private async Task<ItemDisplayRef> DisplayAsync(uint id)
    {
        if (displays.TryGetValue(id, out var known)) return known;
        var (entry, columns, schema) = await SchemaAsync(DisplayTable).ConfigureAwait(false);
        ItemDisplayRef result;
        if (columns is null) result = new(ItemRefState.TableNotReadable, id, null, [], entry);
        else
        {
            int[] models = [Field(columns, schema, "ModelResourcesID", 0), Field(columns, schema, "ModelResourcesID", 1)];
            int[] materials = [Field(columns, schema, "ModelMaterialResourcesID", 0), Field(columns, schema, "ModelMaterialResourcesID", 1)];
            if (models.Concat(materials).Any(index => index < 0))
                result = new(ItemRefState.Unsupported, id, null, [], entry, null, ProfileUnsupportedCode,
                    "ItemDisplayInfo has no plain integer ModelResourcesID[2] and ModelMaterialResourcesID[2] of at most 32 bits.");
            else
            {
                var found = await tables.GetAsync(TableRef.ForName(DisplayTable), id, priority, cancellationToken).ConfigureAwait(false);
                if (found.Outcome != TableRowOutcome.Found) result = new(RefState(found.Outcome), id, null, [], found.Table, found.ProtectedKey);
                else
                {
                    var slots = new List<ItemDisplaySlot>(2);
                    for (var slot = 0; slot < 2; slot++)
                        slots.Add(new(slot, await ResourceAsync(ItemResourceKind.Model, slot, Integer(found.Row!.Row, models[slot])).ConfigureAwait(false),
                            await ResourceAsync(ItemResourceKind.Material, slot, Integer(found.Row!.Row, materials[slot])).ConfigureAwait(false)));
                    result = new(ItemRefState.Found, id, found.Row, slots, found.Table);
                }
            }
        }
        displays[id] = result;
        return result;
    }

    private async Task<ItemResourceRef> ResourceAsync(ItemResourceKind kind, int slot, long? id)
    {
        if (id is not { } value || value < 0 || value > uint.MaxValue)
            return new(kind, slot, id, ItemResourceState.InvalidValue, false, [], 0, null, null, ValueInvalidCode,
                "An ItemDisplayInfo resource cell is not a canonical integer in 0..uint32.");
        if (value == 0) return new(kind, slot, 0, ItemResourceState.NoResource, true, [], 0, null);
        if (resources.TryGetValue((kind, value), out var known)) return known with { Slot = slot };
        var (table, column) = kind == ItemResourceKind.Model ? (ModelTable, "ModelResourcesID") : (TextureTable, "MaterialResourcesID");
        var (entry, columns, schema) = await SchemaAsync(table).ConfigureAwait(false);
        ItemResourceRef result;
        if (columns is null) result = new(kind, slot, value, ItemResourceState.TableNotReadable, false, [], 0, entry);
        else if (Field(columns, schema, column) < 0)
            result = new(kind, slot, value, ItemResourceState.Unsupported, false, [], 0, entry, null, ProfileUnsupportedCode,
                $"{table} has no one plain integer {column} of at most 32 bits.");
        else
        {
            var scan = await relations.LookupAsync(table, column, (uint)value, options.MaxResourceFiles, priority, cancellationToken).ConfigureAwait(false);
            var files = new List<ItemResourceFile>(scan.Rows.Count);
            foreach (var row in scan.Rows)
                files.Add(new(row.Row.Id, row, await ComponentAsync(kind, row.Row.Id).ConfigureAwait(false)));
            var state = scan.State switch
            {
                RelationLookupState.Ok when scan.Matches > options.MaxResourceFiles => ItemResourceState.RowLimit,
                RelationLookupState.Ok when files.Count > 0 => ItemResourceState.Found,
                RelationLookupState.Ok => scan.Complete ? ItemResourceState.Absent : ItemResourceState.Unknown,
                RelationLookupState.IndexLimit => ItemResourceState.IndexLimit,
                RelationLookupState.Stale => ItemResourceState.Stale,
                RelationLookupState.NotReadable => ItemResourceState.TableNotReadable,
                _ => ItemResourceState.Unsupported,
            };
            var keys = scan.Table?.State == TableState.Partial && scan.Table.Reason?.KeyNames is { Count: > 0 } names ? names : null;
            var (code, message) = state == ItemResourceState.RowLimit
                ? (RowLimitCode, $"{scan.Matches} {table} rows have {column} {value}; a detail keeps {options.MaxResourceFiles}.")
                : (scan.Code, scan.Message);
            result = new(kind, slot, value, state, scan.Complete && state != ItemResourceState.RowLimit, files, scan.Matches, scan.Table ?? entry, keys, code, message);
        }
        resources[(kind, value)] = result;
        return result;
    }

    private async Task<ItemComponentKey> ComponentAsync(ItemResourceKind kind, uint fileDataId)
    {
        if (components.TryGetValue((kind, fileDataId), out var known)) return known;
        var table = kind == ItemResourceKind.Model ? ComponentModelTable : ComponentTextureTable;
        var (entry, columns, schema) = await SchemaAsync(table).ConfigureAwait(false);
        ItemComponentKey result;
        if (columns is null) result = new(ItemRefState.TableNotReadable, Table: entry);
        else
        {
            var race = Field(columns, schema, "RaceID");
            var gender = Field(columns, schema, "GenderIndex");
            var cls = Field(columns, schema, "ClassID");
            var position = kind == ItemResourceKind.Model ? Field(columns, schema, "PositionIndex") : -1;
            if (race < 0 || gender < 0 || cls < 0 || (kind == ItemResourceKind.Model && position < 0))
                result = new(ItemRefState.Unsupported, Table: entry, Code: ProfileUnsupportedCode,
                    Message: $"{table} has no one plain integer RaceID, GenderIndex, ClassID{(kind == ItemResourceKind.Model ? " and PositionIndex" : "")} of at most 32 bits.");
            else
            {
                var found = await tables.GetAsync(TableRef.ForName(table), fileDataId, priority, cancellationToken).ConfigureAwait(false);
                result = found.Outcome != TableRowOutcome.Found ? new(RefState(found.Outcome), Table: found.Table, ProtectedKey: found.ProtectedKey)
                    : new(ItemRefState.Found, Integer(found.Row!.Row, race), Integer(found.Row.Row, gender), Integer(found.Row.Row, cls),
                        position < 0 ? null : Integer(found.Row.Row, position), found.Row, found.Table);
            }
        }
        components[(kind, fileDataId)] = result;
        return result;
    }

    private static ItemRefState RefState(TableRowOutcome outcome) => outcome switch
    {
        TableRowOutcome.Absent => ItemRefState.Absent,
        TableRowOutcome.Protected => ItemRefState.Protected,
        _ => ItemRefState.Unknown,
    };

    private async Task<(TableEntry Entry, IReadOnlyList<Db2Column>? Columns, TableSchema? Schema)> SchemaAsync(string table)
    {
        if (!schemas.TryGetValue(table, out var result))
        {
            result = await tables.SchemaAsync(TableRef.ForName(table), priority, cancellationToken).ConfigureAwait(false);
            schemas[table] = result;
        }
        return (result.Table, TableQueries.IsReadable(result.Table.State) ? result.Columns ?? [] : null, result.Schema);
    }

    /// <summary>
    /// The one plain integer header column of the field (<c>Name[slot]</c> for an array element), or -1. When the chosen
    /// definition declares the field (D101), it must be a verified int of at most 32 bits, scalar or (for a slot) of length 2.
    /// </summary>
    internal static int Field(IReadOnlyList<Db2Column> columns, TableSchema? schema, string name, int? slot = null)
    {
        var header = slot is { } index ? $"{name}[{index}]" : name;
        var found = -1;
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i].Name != header || columns[i].Kind != "integer" || columns[i].Interpretation is not null) continue;
            if (found >= 0) return -1;
            found = i;
        }
        if (found < 0) return -1;
        var declared = schema?.Columns.FirstOrDefault(column => column.Name == name);
        if (declared is null) return found;
        var length = slot is null ? 1 : 2;
        return declared is { Kind: ColumnKind.Int, VerifiedName: true, Bits: <= 32 } && declared.ArrayLength == length ? found : -1;
    }

    private static long? Integer(Db2Row row, int index) =>
        index < row.Values.Count && TableQueries.TryParseInteger(row.Values[index], out var value) && value >= long.MinValue && value <= long.MaxValue
            ? (long)value : null;

    private static uint? Uint(Db2Row row, int index) => Integer(row, index) is long value and >= 0 and <= uint.MaxValue ? (uint)value : null;
}
