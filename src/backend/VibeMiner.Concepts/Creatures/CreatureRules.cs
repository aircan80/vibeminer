using System.Globalization;
using VibeMiner.Formats;
using VibeMiner.Formats.Definitions;
using VibeMiner.Formats.Images;
using VibeMiner.Formats.Models;

namespace VibeMiner.Concepts.Creatures;

/// <summary>A Creature column-shape or cell problem: one of the old Creatures codes, kept by name. Caught at the call and returned as a state.</summary>
internal sealed class CreaturesSourceException(string code, string message) : Exception(message)
{
    public const string SchemaUnsupported = "CREATURES_SCHEMA_UNSUPPORTED";
    public const string SourceInvalid = "CREATURES_PROJECTION_INVALID";

    public string Code { get; } = code;

    public static CreaturesSourceException Unsupported(string why) => new(SchemaUnsupported, "The Creature definition doesn't qualify the sixteen named fields: " + why + ".");
    public static CreaturesSourceException Invalid(string why) => new(SourceInvalid, "A Creature row doesn't fit its fields: " + why + ".");
}

/// <summary>
/// Which header columns the Creature rules read (creatures-core-v1's sixteen fields, D101, D462): each picked by name, with its
/// kind, width and sign checked against the definition layer 5 chose; the layout isn't pinned. -1 never appears: a field the
/// definition or header doesn't give is <see cref="CreaturesSourceException.SchemaUnsupported"/>.
/// </summary>
internal sealed record CreatureLayout(int ColumnCount, int Name, IReadOnlyList<(string Column, int Index, int Bits, bool Unsigned)> Facts,
    int[] Displays, int[] Probabilities)
{
    public const string Table = "Creature";
    private const string Strings = "string", Integer = "integer", Float = "float";

    // Name, kind, bits, unsigned, array length: exactly as CreaturesPolicy.ValidateBinding.
    private static readonly (string Name, string Kind, int? Bits, bool? Unsigned, int Array)[] Fields =
    [
        ("Name_lang", Strings, null, null, 1), ("NameAlt_lang", Strings, null, null, 1), ("Title_lang", Strings, null, null, 1),
        ("TitleAlt_lang", Strings, null, null, 1), ("Classification", Integer, 8, false, 1), ("CreatureType", Integer, 8, true, 1),
        ("CreatureFamily", Integer, 16, true, 1), ("StartAnimState", Integer, 8, false, 1), ("DisplayID", Integer, 32, false, 4),
        ("DisplayProbability", Float, null, null, 4),
    ];

    public static CreatureLayout Resolve(IReadOnlyList<Db2Column> columns, TableSchema? schema)
    {
        if (schema is null) throw CreaturesSourceException.Unsupported("no definition resolves");
        var picked = new Dictionary<string, int[]>();
        foreach (var (name, kind, bits, unsigned, array) in Fields)
        {
            var matches = schema.Columns.Where(column => column.Name == name).ToArray();
            if (matches.Length != 1) throw CreaturesSourceException.Unsupported($"{name} isn't one definition field");
            var field = matches[0];
            var family = field.Kind switch { ColumnKind.Int => Integer, ColumnKind.Float => Float, _ => Strings };
            if (!field.VerifiedName || field.ArrayLength != array || family != kind || field.Bits != bits || field.Unsigned != unsigned)
                throw CreaturesSourceException.Unsupported($"{name} isn't a verified {kind}{(bits is null ? "" : $" of {bits} bits, {(unsigned == true ? "unsigned" : "signed")}")} with {array} cell(s)");
            picked[name] = [.. Enumerable.Range(0, array).Select(slot => Column(columns, array == 1 ? name : $"{name}[{slot}]", kind))];
        }
        return new(columns.Count, picked["Name_lang"][0],
            [.. Fields[4..8].Select(field => (field.Name, picked[field.Name][0], field.Bits!.Value, field.Unsigned!.Value))],
            picked["DisplayID"], picked["DisplayProbability"]);
    }

    private static int Column(IReadOnlyList<Db2Column> columns, string name, string kind)
    {
        var found = -1;
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i].Name != name) continue;
            if (found >= 0) throw CreaturesSourceException.Unsupported($"{name} has more than one header column");
            found = i;
        }
        if (found < 0 || columns[found] is not { IsId: false, Interpretation: null } column || column.Kind != kind)
            throw CreaturesSourceException.Unsupported($"{name} isn't one plain {kind} header column");
        return found;
    }

    /// <summary>The name alone: Invalid on its one DB2_TEXT_ENCODING_INVALID issue, Empty for "", else Valid (whitespace too).</summary>
    public CreatureName NameOf(Db2Row row)
    {
        Check(row);
        var issues = row.TextIssues?.Where(issue => issue.ColumnIndex == Name).ToArray() ?? [];
        if (issues.Length > 1 || issues.Any(issue => issue.Code != "DB2_TEXT_ENCODING_INVALID"))
            throw CreaturesSourceException.Invalid("the name has an unexpected text issue");
        if (issues.Length == 1) return new(CreatureNameState.Invalid, null, issues[0].Code);
        var value = row.Values[Name];
        return value.Length == 0 ? new(CreatureNameState.Empty, "") : new(CreatureNameState.Valid, value);
    }

    /// <summary>The summary of a Creature row; every fact, display and probability cell is checked as the old projection did.</summary>
    public CreatureSummary Summary(Db2Row row)
    {
        var name = NameOf(row);
        var facts = new CreatureSourceValue[Facts.Count];
        for (var i = 0; i < facts.Length; i++)
        {
            var (column, index, bits, unsigned) = Facts[i];
            var text = row.Values[index];
            if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) || N(value) != text ||
                value < (unsigned ? 0 : -(1L << (bits - 1))) || value > (unsigned ? (1L << bits) - 1 : (1L << (bits - 1)) - 1))
                throw CreaturesSourceException.Invalid($"{column} isn't a canonical value of its width");
            facts[i] = new(column, index, text);
        }
        var slots = new CreatureDisplaySlot[4];
        for (var slot = 0; slot < 4; slot++)
        {
            var display = row.Values[Displays[slot]];
            var probability = row.Values[Probabilities[slot]];
            if (!int.TryParse(display, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id) || N(id) != display)
                throw CreaturesSourceException.Invalid($"DisplayID[{slot}] isn't a canonical int32");
            if (!float.TryParse(probability, NumberStyles.Float, CultureInfo.InvariantCulture, out var chance) || !float.IsFinite(chance) ||
                chance.ToString("R", CultureInfo.InvariantCulture) != probability)
                throw CreaturesSourceException.Invalid($"DisplayProbability[{slot}] isn't a finite float32 as read");
            slots[slot] = new(slot, id, chance, id switch { > 0 => CreatureSlotState.Declared, 0 => CreatureSlotState.NoDeclaredDisplay, _ => CreatureSlotState.InvalidDisplayReference });
        }
        return new(row.Id, row.IsCopy, row.CopySourceId, name, facts, slots);
    }

    private void Check(Db2Row row)
    {
        if (row.Values.Count != ColumnCount || row.IsCopy != row.CopySourceId.HasValue ||
            row.TextIssues?.Any(issue => issue.ColumnIndex < 0 || issue.ColumnIndex >= row.Values.Count) == true)
            throw CreaturesSourceException.Invalid("the row doesn't line up with its header");
    }

    // Reviewed on #62 (WoW review): Creature.Classification is signed8; values outside 0-6 have no label.
    private static readonly string[] ClassificationLabels = ["Normal", "Elite", "Rare elite", "World boss", "Rare", "Trivial", "Minus"];

    /// <summary>The label of an exact decimal Classification 0 to 6 (creature-labels-v1), else null.</summary>
    public static string? ClassificationLabel(string value) =>
        value is { Length: 1 } && value[0] is >= '0' and <= '6' ? ClassificationLabels[value[0] - '0'] : null;

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The two tables of a display chain.</summary>
internal enum DisplayRole { Display, Model }

/// <summary>The old CreatureDisplayPolicy and CreatureDisplayFacts rules on layer 5 rows (D088, D089, D107, D463).</summary>
internal static class CreatureDisplayRules
{
    public const string DisplayTable = "CreatureDisplayInfo", ModelTable = "CreatureModelData";
    public const string Unsupported = "CREATURE_DISPLAY_SCHEMA_UNSUPPORTED", Integrity = "CREATURE_DISPLAY_INTEGRITY";
    public const int MaxCells = 512, MaxFactCells = 16;
    private const string ExtendedField = "ExtendedDisplayInfoID", VariationField = "TextureVariationFileDataID";
    private static readonly (string Key, string Field)[] DisplayFacts = [("model", "ModelID"), ("scale", "CreatureModelScale"), ("textureVariations", VariationField)];
    private static readonly (string Key, string Field)[] ModelFacts = [("file", "FileDataID"), ("scale", "ModelScale")];

    public static string Table(DisplayRole role) => role == DisplayRole.Display ? DisplayTable : ModelTable;
    public static string Field(DisplayRole role) => role == DisplayRole.Display ? "ModelID" : "FileDataID";

    /// <summary>Retail layouts, then the Classic layouts (F75F884C/3FFEE408) that 1.15, 2.5 and 5.5 builds share (D089).</summary>
    public static IReadOnlyList<string> Layouts(DisplayRole role) => role == DisplayRole.Display ? ["7275F5F6", "F75F884C"] : ["0F5449F8", "3FFEE408"];

    /// <summary>
    /// The edge column (ModelID u16, FileDataID u32): the layout must be a reviewed one, the definition field one verified
    /// unsigned scalar of that width that isn't the ID or a relation, and the header column one plain integer. Null means unsupported.
    /// </summary>
    public static int? EdgeColumn(DisplayRole role, string? layout, IReadOnlyList<Db2Column> columns, TableSchema? schema)
    {
        if (layout is null || !Layouts(role).Contains(layout.ToUpperInvariant()) || schema is null) return null;
        var name = Field(role);
        var fields = schema.Columns.Where(column => column.Name == name).ToArray();
        if (fields is not [{ Kind: ColumnKind.Int, Unsigned: true, ArrayLength: 1, VerifiedName: true, Id: KeyStorage.None, Relation: KeyStorage.None } field] ||
            field.Bits != (role == DisplayRole.Display ? 16 : 32)) return null;
        var index = -1;
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i].Name != name) continue;
            if (index >= 0) return null;
            index = i;
        }
        return index >= 0 && columns[index] is { Kind: "integer", IsId: false, Interpretation: null } ? index : null;
    }

    /// <summary>The edge value: a canonical uint with no text issue, at most 65535 for ModelID, on the row asked for. Null is CREATURE_DISPLAY_INTEGRITY.</summary>
    public static uint? Edge(Db2Row row, int column, DisplayRole role, uint id)
    {
        if (row.Id != id || row.IsCopy != row.CopySourceId.HasValue || column < 0 || column >= row.Values.Count ||
            row.TextIssues?.Any(issue => issue.ColumnIndex == column) == true ||
            !uint.TryParse(row.Values[column], NumberStyles.None, CultureInfo.InvariantCulture, out var edge) ||
            edge.ToString(CultureInfo.InvariantCulture) != row.Values[column] || role == DisplayRole.Display && edge > ushort.MaxValue) return null;
        return edge;
    }

    /// <summary>
    /// The row's reviewed facts picked by base field name, so Retail and Classic layouts give the same keys; a field the layout
    /// lacks is Unsupported, never zero; every other non-ID column follows as Raw.
    /// </summary>
    public static CreatureRecordFacts Facts(DisplayRole role, IReadOnlyList<Db2Column> columns, Db2Row row)
    {
        if (columns.Count > MaxCells) return new(CreatureFactsState.LimitExceeded, [], [], "CREATURES_RESULT_LIMIT");
        if (row.TextIssues is { Count: > 0 } || row.Values.Count != columns.Count) return new(CreatureFactsState.RowNotWhole, [], []);
        var used = new HashSet<int>();
        var facts = new List<CreatureFact>();
        foreach (var (key, field) in role == DisplayRole.Display ? DisplayFacts : ModelFacts)
        {
            var cells = new List<CreatureFactCell>();
            for (var i = 0; i < columns.Count; i++)
                if (BaseName(columns[i].Name) == field && !columns[i].IsId) { cells.Add(new(columns[i].Name, i, row.Values[i])); used.Add(i); }
            // A fact holds 16 cells; a wider array is refused whole, never cut.
            if (cells.Count > MaxFactCells) return new(CreatureFactsState.LimitExceeded, [], [], "CREATURES_RESULT_LIMIT");
            facts.Add(new(key, cells.Count == 0 ? CreatureFactState.Unsupported : CreatureFactState.Value, cells));
        }
        var raw = new List<CreatureFactCell>();
        for (var i = 0; i < columns.Count; i++)
            if (!used.Contains(i) && !columns[i].IsId) raw.Add(new(columns[i].Name, i, row.Values[i]));
        return new(CreatureFactsState.Ready, facts, raw);
    }

    private static string BaseName(string column) { var open = column.IndexOf('['); return open < 0 ? column : column[..open]; }

    /// <summary>The display row's texture fields; null when one is absent, not one plain integer column, has a text issue or isn't a canonical int32.</summary>
    public static CreatureTextureEvidence? TextureEvidence(IReadOnlyList<Db2Column> columns, Db2Row row)
    {
        int? Value(string name)
        {
            var index = -1;
            for (var i = 0; i < columns.Count; i++)
                if (columns[i].Name == name) { if (index != -1) return null; index = i; }
            if (index < 0 || columns[index] is not { Kind: "integer", IsId: false, Interpretation: null } || index >= row.Values.Count ||
                row.TextIssues?.Any(issue => issue.ColumnIndex == index) == true) return null;
            var text = row.Values[index];
            return int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) &&
                value.ToString(CultureInfo.InvariantCulture) == text ? value : null;
        }
        var extended = Value(ExtendedField);
        var variations = Enumerable.Range(0, 4).Select(i => Value($"{VariationField}[{i}]")).ToArray();
        if (extended is null || variations.Any(value => value is null)) return null;
        return new(extended.Value, [.. variations.Select(value => value!.Value)]);
    }

    /// <summary>
    /// The model's textures under D088, every declared one: type 0 is its TXID entry (or, for a legacy name, the file layer 5
    /// found by name), types 11/12/13 are TextureVariationFileDataID[0/1/2]; only a value above 0 resolves, and 0 is
    /// Unresolved, never substituted. <paramref name="drawn"/> are the ordinals the main skin's drawn batches use.
    /// </summary>
    public static IReadOnlyList<CreatureTexture> Textures(ModelRead read, CreatureTextureEvidence? evidence, IReadOnlySet<int>? drawn)
    {
        var textures = read.Model?.Textures ?? [];
        var result = new CreatureTexture[textures.Count];
        for (var i = 0; i < result.Length; i++)
        {
            var texture = textures[i];
            var reference = read.References.FirstOrDefault(r => r.Kind == ModelReferenceKind.Texture && r.Ordinal == texture.Ordinal);
            var used = drawn?.Contains(texture.Ordinal) == true;
            if (texture.Type == 0)
            {
                var id = texture.FileDataId ?? reference?.File?.FileDataId ?? 0;
                result[i] = new(texture.Ordinal, texture.Type, texture.Flags, texture.Filename, CreatureTextureSource.Txid, null, id > 0 ? id : null,
                    id > 0 ? CreatureTextureState.Resolved : CreatureTextureState.Unresolved, used, reference);
            }
            else if (texture.Type is 11 or 12 or 13)
            {
                var slot = (int)texture.Type - 11;
                var value = evidence?.TextureVariations[slot] ?? 0;
                result[i] = new(texture.Ordinal, texture.Type, texture.Flags, texture.Filename, CreatureTextureSource.Variation, slot,
                    value > 0 ? (uint)value : null, value > 0 ? CreatureTextureState.Resolved : CreatureTextureState.Unresolved, used, reference);
            }
            else result[i] = new(texture.Ordinal, texture.Type, texture.Flags, texture.Filename, CreatureTextureSource.Other, null, null,
                CreatureTextureState.ResolvedElsewhere, used, reference);
        }
        return result;
    }

    /// <summary>
    /// Geometry's checks, before any appearance rule (GeometrySources): the geometry profile (a chunked 272 or 274 model with
    /// no external skeleton or bone files, <see cref="M2GeometrySkinReader.ProfileRefusal"/>) is GeometryUnsupported, then no
    /// positive main skin is NoMainSkin; both MODEL_UNSUPPORTED. Null means read the skin.
    /// </summary>
    public static CreatureAppearance? GeometryRefusal(M2Model model)
    {
        if (M2GeometrySkinReader.ProfileRefusal(model) is { } profile) return new(CreatureAppearanceState.GeometryUnsupported, "MODEL_UNSUPPORTED", profile);
        return model.SkinFileDataIds.Count == 0 || model.SkinFileDataIds[0] == 0
            ? new(CreatureAppearanceState.NoMainSkin, "MODEL_UNSUPPORTED", "The model has no positive declared main SKIN.") : null;
    }

    /// <summary>What the main skin's batches draw: the texture ordinals, the blended batches counted, and the first refusal.</summary>
    public sealed record SkinBatches(IReadOnlySet<int> Drawn, int Blended, CreatureAppearance? Refusal);

    /// <summary>
    /// The texture ordinals the main skin's drawn batches use (M2Appearance.AdmitAsync's chain): a batch's stages are
    /// <c>TextureCombos[TextureComboIndex + s]</c> for each of its <c>TextureCount</c> stages; a batch whose material blends
    /// with mode 2-7 is counted, not drawn (D094), so its textures don't count. Refused, in batch order, as MaterialUnsupported:
    /// a batch naming a section, material or texture lookup the model doesn't declare, or a drawn texture
    /// with flags outside the U/V wrap bits or whose type has no creature binding (not 0 or 11-13). A blend mode above 7 is BlendUnsupported. When no batch refuses but
    /// none draws a surface triangle, BlendUnsupported names the first blended batch with triangles, else MaterialUnsupported.
    /// The other plan refusals belong to rendering (D465).
    /// </summary>
    public static SkinBatches DrawnTextures(M2Model model, M2Skin skin)
    {
        var drawn = new SortedSet<int>();
        var blended = 0; var draws = 0; int? firstBlended = null;
        for (var i = 0; i < skin.Batches.Count; i++)
        {
            var batch = skin.Batches[i];
            CreatureAppearance Refuse(string message, int? texture = null) =>
                new(CreatureAppearanceState.MaterialUnsupported, "CREATURE_APPEARANCE_MATERIAL_UNSUPPORTED", message, texture, i);
            if (batch.SectionIndex >= skin.Sections.Count || batch.MaterialIndex >= model.Materials.Count)
                return new(drawn, blended, Refuse("The batch names a section or material the model does not declare."));
            var blend = model.Materials[batch.MaterialIndex].BlendMode;
            if (blend > 7)
                return new(drawn, blended, new(CreatureAppearanceState.BlendUnsupported, "CREATURE_APPEARANCE_BLEND_UNSUPPORTED",
                    $"The batch blends with mode {N(blend)}, which has no M2 meaning.", BatchOrdinal: i));
            var triangles = skin.Sections[batch.SectionIndex].TriangleCount != 0;
            if (blend >= 2) { blended++; if (triangles) firstBlended ??= i; continue; }
            if (batch.TextureComboIndex + batch.TextureCount > model.TextureCombos.Count ||
                Enumerable.Range(batch.TextureComboIndex, batch.TextureCount).Any(c => model.TextureCombos[c] >= model.Textures.Count))
                return new(drawn, blended, Refuse("The batch texture lookup names no declared texture."));
            for (var stage = 0; stage < batch.TextureCount; stage++)
            {
                var ordinal = model.TextureCombos[batch.TextureComboIndex + stage];
                var texture = model.Textures[ordinal];
                if ((texture.Flags & ~3u) != 0) return new(drawn, blended, Refuse("The texture has flags outside the U/V wrap bits.", ordinal));
                if (texture.Type != 0 && texture.Type is not (11 or 12 or 13))
                    return new(drawn, blended, Refuse($"Texture type {N(texture.Type)} has no creature binding in this profile.", ordinal));
                drawn.Add(ordinal);
            }
            if (triangles) draws++;
        }
        if (draws > 0) return new(drawn, blended, null);
        var none = firstBlended is { } layer
            ? new CreatureAppearance(CreatureAppearanceState.BlendUnsupported, "CREATURE_APPEARANCE_BLEND_UNSUPPORTED",
                $"Every batch with surface triangles is a blended layer (the first blends with mode {N(model.Materials[skin.Batches[layer].MaterialIndex].BlendMode)}); no opaque or alpha-key surface is left to draw.",
                BatchOrdinal: layer)
            : new(CreatureAppearanceState.MaterialUnsupported, "CREATURE_APPEARANCE_MATERIAL_UNSUPPORTED", "No batch draws any declared surface triangle.");
        return new(drawn, blended, none);
    }

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The appearance refusals checked before any texture is read, in the old order: no texture evidence, a character model,
    /// a batch refusal from <see cref="DrawnTextures"/> (or no drawn surface), then the first drawn creature texture with no FileDataID. Null means go
    /// on to the textures. An undrawn texture never refuses.
    /// </summary>
    public static CreatureAppearance? Refusal(CreatureTextureEvidence? evidence, IReadOnlyList<CreatureTexture> textures, CreatureAppearance? batchRefusal)
    {
        if (evidence is null)
            return new(CreatureAppearanceState.TextureUnresolved, "CREATURE_DISPLAY_TEXTURES_UNREADABLE",
                "The display row has no readable ExtendedDisplayInfoID and four texture variation fields.");
        if (evidence.ExtendedDisplayInfoId != 0)
            return new(CreatureAppearanceState.CharacterModel, "CREATURE_APPEARANCE_CHARACTER_MODEL",
                $"This display is a character model (ExtendedDisplayInfoID {evidence.ExtendedDisplayInfoId.ToString(CultureInfo.InvariantCulture)}); its baked textures are not resolved.");
        if (batchRefusal is not null) return batchRefusal;
        if (textures.FirstOrDefault(texture => texture.Drawn && texture.State == CreatureTextureState.Unresolved) is { } missing)
            return new(CreatureAppearanceState.TextureUnresolved, "CREATURE_APPEARANCE_TEXTURE_UNRESOLVED",
                $"Texture ordinal {missing.Ordinal.ToString(CultureInfo.InvariantCulture)} has a zero FileDataID in its TXID entry or display variation; nothing is substituted.",
                missing.Ordinal);
        return null;
    }

    /// <summary>The appearance state of the first texture whose header didn't read.</summary>
    public static CreatureAppearanceState TextureFailure(BlpImageState state) => state switch
    {
        BlpImageState.Protected => CreatureAppearanceState.TextureProtected,
        BlpImageState.Unsupported => CreatureAppearanceState.TextureUnsupported,
        BlpImageState.LimitExceeded or BlpImageState.MipUnavailable => CreatureAppearanceState.TextureLimit,
        BlpImageState.Invalid => CreatureAppearanceState.TextureCorrupt,
        _ => CreatureAppearanceState.TextureUnavailable,
    };
}
