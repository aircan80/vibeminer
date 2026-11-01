namespace VibeMiner.Formats.Definitions;

/// <summary>How the block was chosen: <see cref="Build"/> when it lists the exact build, <see cref="Layout"/> when
/// only the file's layout hash chose it (D080). A layout match never verifies names.</summary>
public enum SchemaMatch { Build, Layout }

/// <summary>A column's type, from its DBD family only (never from a name such as a <c>_lang</c> suffix).</summary>
public enum ColumnKind { Int, Float, String, LocString }

/// <summary>Where an id or relation value lives: not marked, in the record, or outside it (the noninline annotation).</summary>
public enum KeyStorage { None, Inline, NonInline }

/// <summary>A foreign key, the target table and column exactly as the definition writes them. The target is that
/// column, which need not be the target's ID.</summary>
public sealed record ForeignKey(string Table, string Column);

/// <summary>One typed column of a selected block. <see cref="Bits"/> and <see cref="Unsigned"/> are set only for
/// <see cref="ColumnKind.Int"/>. <see cref="ArrayLength"/> 1 is a scalar. <see cref="Locale"/> is set only for a
/// <see cref="ColumnKind.LocString"/>: the string is read from that locale's file, never from per-locale slots.
/// <see cref="VerifiedName"/> false is the DBD <c>?</c> suffix (an unconfirmed name, not a nullable value).
/// <see cref="ColumnComment"/> is the COLUMNS line comment, <see cref="FieldComment"/> the block line comment.</summary>
public sealed record SchemaColumn(string Name, ColumnKind Kind, int? Bits, bool? Unsigned, int ArrayLength,
    ForeignKey? ForeignKey, KeyStorage Id, KeyStorage Relation, bool VerifiedName, WowLocale? Locale,
    string? ColumnComment, string? FieldComment)
{
    public bool IsArray => ArrayLength > 1;
}

/// <summary>The typed schema of one selected DBD block: its columns in block order, its layout hashes, how it was
/// matched, the locale its strings are read in and the block comment. Equality compares the lists element-wise.</summary>
public sealed record TableSchema
{
    private TableSchema(IReadOnlyList<SchemaColumn> columns, IReadOnlyList<string> layoutHashes, SchemaMatch match,
        WowLocale locale, string? comment)
    {
        Columns = columns; LayoutHashes = layoutHashes; Match = match; Locale = locale; Comment = comment;
    }

    public IReadOnlyList<SchemaColumn> Columns { get; }
    public IReadOnlyList<string> LayoutHashes { get; }
    public SchemaMatch Match { get; }
    public WowLocale Locale { get; }
    public string? Comment { get; }

    /// <summary>Types every field of <paramref name="block"/>. A field the WoW rules don't admit refuses the whole
    /// block with <c>DEFINITION_BLOCK_UNSUPPORTED</c>; nothing is coerced.</summary>
    public static TableSchema FromBlock(DbdBlock block, SchemaMatch match, WowLocale locale)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(locale);
        if (!Enum.IsDefined(match)) throw new ArgumentOutOfRangeException(nameof(match));
        var columns = new SchemaColumn[block.Fields.Count];
        for (var i = 0; i < columns.Length; i++) columns[i] = Column(block.Fields[i], locale);
        return new(Array.AsReadOnly(columns), Array.AsReadOnly(block.LayoutHashes.ToArray()), match, locale, block.Comment);
    }

    private static SchemaColumn Column(DbdField field, WowLocale locale)
    {
        var column = field.Column;
        var kind = column.Family switch
        {
            "int" => ColumnKind.Int,
            "float" => ColumnKind.Float,
            "string" => ColumnKind.String,
            "locstring" => ColumnKind.LocString,
            _ => throw Refuse(column.Name, $"its type family '{column.Family}' is unknown"),
        };
        if (kind == ColumnKind.Int && (field.IntegerBits is null || field.Unsigned is null))
            throw Refuse(column.Name, "an int field needs a width");
        if (kind != ColumnKind.Int && (field.IntegerBits is not null || field.Unsigned is not null))
            throw Refuse(column.Name, "only an int field takes a width");
        if (field.ArrayLength < 1) throw Refuse(column.Name, "its array length is not positive");
        if (column.Reference is not null && kind != ColumnKind.Int)
            throw Refuse(column.Name, "only an int field can be a foreign key");
        if (field.IsId || field.RelationStorage)
        {
            var role = field.IsId ? "an id" : "a relation";
            if (field.IsId && field.RelationStorage) throw Refuse(column.Name, "a field can't be both the id and a relation");
            if (kind != ColumnKind.Int) throw Refuse(column.Name, $"{role} must be an int field");
            if (field.ArrayLength != 1) throw Refuse(column.Name, $"{role} can't be an array");
        }
        else if (field.NonInline) throw Refuse(column.Name, "noninline needs id or relation");
        if (kind == ColumnKind.LocString && field.ArrayLength != 1) throw Refuse(column.Name, "a locstring can't be an array");
        var storage = field.NonInline ? KeyStorage.NonInline : KeyStorage.Inline;
        return new(column.Name, kind, field.IntegerBits, field.Unsigned, field.ArrayLength,
            column.Reference is { } reference ? new ForeignKey(reference.Table, reference.Column) : null,
            field.IsId ? storage : KeyStorage.None, field.RelationStorage ? storage : KeyStorage.None,
            column.VerifiedName, kind == ColumnKind.LocString ? locale : null, column.Comment, OwnComment(field));
    }

    // The reader joins the column comment and the field's own line comment with a newline; a line comment never
    // holds a newline, so the field's own part is whatever follows the column comment.
    private static string? OwnComment(DbdField field)
    {
        if (field.Comment is not { } joined) return null;
        if (field.Column.Comment is not { } column) return joined;
        if (joined == column) return null;
        return joined.StartsWith(column + "\n", StringComparison.Ordinal) ? joined[(column.Length + 1)..] : joined;
    }

    private static DefinitionException Refuse(string field, string why) =>
        new("DEFINITION_BLOCK_UNSUPPORTED", $"The definition block can't be used: field '{field}': {why}.");

    public bool Equals(TableSchema? other) =>
        other is not null && Match == other.Match && Locale.Equals(other.Locale) && Comment == other.Comment &&
        Columns.SequenceEqual(other.Columns) && LayoutHashes.SequenceEqual(other.LayoutHashes, StringComparer.Ordinal);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Match); hash.Add(Locale); hash.Add(Comment);
        foreach (var column in Columns) hash.Add(column);
        foreach (var layout in LayoutHashes) hash.Add(layout, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}
