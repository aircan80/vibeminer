using System.Text;
using VibeMiner.Formats.Definitions;
using Xunit;

namespace VibeMiner.Formats.Definitions.Tests;

public sealed class TableSchemaTests
{
    private const string Source = """
        COLUMNS
        int ID // row key
        int<Map::ID> MapID
        int<Spell::SpellID> SpellRef? // guessed name
        int Flags
        int Small
        int Wide
        int Signed8
        float Scale
        float Position
        string Model
        string Paths
        locstring Name_lang
        string Plain_lang
        int ParentID

        LAYOUT ABCD1234, 0000BEEF
        BUILD 11.0.7.58123
        COMMENT block note
        $noninline,id$ID<32>
        MapID<u16>
        SpellRef<32> // field note
        Flags<u32>[3]
        Small<u8>
        Wide<u64>
        Signed8<8>
        Scale
        Position[3]
        Model
        Paths[2]
        Name_lang
        Plain_lang
        $noninline,relation$ParentID<32>
        """;

    private static DbdBlock Block(string source, string build = "11.0.7.58123") =>
        Assert.Single(DbdDefinitionReader.Parse(Encoding.UTF8.GetBytes(source)).SelectBuild(build).Blocks);

    private static string Single(string columns, string field) => $"COLUMNS\n{columns}\n\nBUILD 1.0.0.1\n{field}\n";

    private static DefinitionException Refused(string columns, string field)
    {
        var block = Block(Single(columns, field), "1.0.0.1");
        var error = Assert.Throws<DefinitionException>(() => TableSchema.FromBlock(block, SchemaMatch.Build, WowLocale.EnUS));
        Assert.Equal("DEFINITION_BLOCK_UNSUPPORTED", error.Code);
        Assert.False(error.Retryable);
        return error;
    }

    [Fact]
    public void Types_every_column_in_block_order()
    {
        var schema = TableSchema.FromBlock(Block(Source), SchemaMatch.Build, WowLocale.DeDE);
        Assert.Equal(["ID", "MapID", "SpellRef", "Flags", "Small", "Wide", "Signed8", "Scale", "Position", "Model", "Paths",
            "Name_lang", "Plain_lang", "ParentID"], schema.Columns.Select(column => column.Name));
        Assert.Equal(["ABCD1234", "0000BEEF"], schema.LayoutHashes);
        Assert.Equal(SchemaMatch.Build, schema.Match);
        Assert.Same(WowLocale.DeDE, schema.Locale);
        Assert.Equal("block note", schema.Comment);
        var c = schema.Columns.ToDictionary(column => column.Name);

        Assert.Equal(new SchemaColumn("ID", ColumnKind.Int, 32, false, 1, null, KeyStorage.NonInline, KeyStorage.None, true, null,
            "row key", null), c["ID"]);
        Assert.Equal(new SchemaColumn("MapID", ColumnKind.Int, 16, true, 1, new ForeignKey("Map", "ID"), KeyStorage.None, KeyStorage.None,
            true, null, null, null), c["MapID"]);
        Assert.Equal((ColumnKind.Int, 8, true), (c["Small"].Kind, c["Small"].Bits, c["Small"].Unsigned));
        Assert.Equal((ColumnKind.Int, 64, true), (c["Wide"].Kind, c["Wide"].Bits, c["Wide"].Unsigned));
        Assert.Equal((ColumnKind.Int, 8, false), (c["Signed8"].Kind, c["Signed8"].Bits, c["Signed8"].Unsigned));
        Assert.Equal((ColumnKind.Int, 32, true, 3), (c["Flags"].Kind, c["Flags"].Bits, c["Flags"].Unsigned, c["Flags"].ArrayLength));
        Assert.True(c["Flags"].IsArray);
        Assert.Equal(new SchemaColumn("Scale", ColumnKind.Float, null, null, 1, null, KeyStorage.None, KeyStorage.None, true, null, null, null),
            c["Scale"]);
        Assert.False(c["Scale"].IsArray);
        Assert.Equal((ColumnKind.Float, 3), (c["Position"].Kind, c["Position"].ArrayLength));
        Assert.Equal((ColumnKind.String, 1, (WowLocale?)null), (c["Model"].Kind, c["Model"].ArrayLength, c["Model"].Locale));
        Assert.Equal((ColumnKind.String, 2), (c["Paths"].Kind, c["Paths"].ArrayLength));
        Assert.Equal(new SchemaColumn("Name_lang", ColumnKind.LocString, null, null, 1, null, KeyStorage.None, KeyStorage.None, true,
            WowLocale.DeDE, null, null), c["Name_lang"]);
        Assert.Equal(new SchemaColumn("ParentID", ColumnKind.Int, 32, false, 1, null, KeyStorage.None, KeyStorage.NonInline, true, null,
            null, null), c["ParentID"]);
    }

    [Fact]
    public void Type_comes_from_the_family_never_from_a_lang_suffix()
    {
        var c = TableSchema.FromBlock(Block(Source), SchemaMatch.Build, WowLocale.EnUS).Columns.ToDictionary(column => column.Name);
        Assert.Equal(ColumnKind.String, c["Plain_lang"].Kind);
        Assert.Null(c["Plain_lang"].Locale);
        var locstring = TableSchema.FromBlock(Block(Single("locstring Title", "Title"), "1.0.0.1"), SchemaMatch.Build, WowLocale.KoKR);
        Assert.Equal((ColumnKind.LocString, WowLocale.KoKR), (locstring.Columns[0].Kind, locstring.Columns[0].Locale));
    }

    [Fact]
    public void Foreign_key_keeps_the_written_target_column_not_the_target_id()
    {
        var c = TableSchema.FromBlock(Block(Source), SchemaMatch.Build, WowLocale.EnUS).Columns.ToDictionary(column => column.Name);
        Assert.Equal(new ForeignKey("Spell", "SpellID"), c["SpellRef"].ForeignKey);
        Assert.Null(c["Flags"].ForeignKey);
        var cased = TableSchema.FromBlock(Block(Single("int<spellMisc::someColumn> Ref", "Ref<32>"), "1.0.0.1"), SchemaMatch.Build, WowLocale.EnUS);
        Assert.Equal(new ForeignKey("spellMisc", "someColumn"), cased.Columns[0].ForeignKey);
    }

    [Fact]
    public void Keeps_the_four_annotation_forms_apart()
    {
        var source = """
            COLUMNS
            int A
            int B
            int C
            int D

            BUILD 1.0.0.1
            $id$A<32>
            $noninline,id$B<32>
            $relation$C<u16>
            $noninline,relation$D<32>
            """;
        var c = TableSchema.FromBlock(Block(source, "1.0.0.1"), SchemaMatch.Build, WowLocale.EnUS).Columns;
        Assert.Equal((KeyStorage.Inline, KeyStorage.None), (c[0].Id, c[0].Relation));
        Assert.Equal((KeyStorage.NonInline, KeyStorage.None), (c[1].Id, c[1].Relation));
        Assert.Equal((KeyStorage.None, KeyStorage.Inline), (c[2].Id, c[2].Relation));
        Assert.Equal((KeyStorage.None, KeyStorage.NonInline), (c[3].Id, c[3].Relation));
        Assert.Equal(KeyStorage.NonInline, TableSchema.FromBlock(Block(Single("int B", "$id,noninline$B<32>"), "1.0.0.1"), SchemaMatch.Build,
            WowLocale.EnUS).Columns[0].Id);
    }

    [Fact]
    public void Keeps_column_field_and_block_comments_apart()
    {
        var c = TableSchema.FromBlock(Block(Source), SchemaMatch.Build, WowLocale.EnUS).Columns.ToDictionary(column => column.Name);
        Assert.Equal(("guessed name", "field note"), (c["SpellRef"].ColumnComment, c["SpellRef"].FieldComment));
        Assert.Equal(("row key", (string?)null), (c["ID"].ColumnComment, c["ID"].FieldComment));
        Assert.Equal(((string?)null, (string?)null), (c["MapID"].ColumnComment, c["MapID"].FieldComment));
        var fieldOnly = TableSchema.FromBlock(Block(Single("int A", "A<32> // only here"), "1.0.0.1"),
            SchemaMatch.Build, WowLocale.EnUS);
        Assert.Equal(((string?)null, "only here"), (fieldOnly.Columns[0].ColumnComment, fieldOnly.Columns[0].FieldComment));
        Assert.Null(fieldOnly.Comment);
        var same = TableSchema.FromBlock(Block(Single("int A // same", "A<32> // same"), "1.0.0.1"), SchemaMatch.Build, WowLocale.EnUS);
        Assert.Equal(("same", "same"), (same.Columns[0].ColumnComment, same.Columns[0].FieldComment));
    }

    [Fact]
    public void The_question_mark_suffix_marks_an_unverified_name_not_a_nullable_value()
    {
        var c = TableSchema.FromBlock(Block(Source), SchemaMatch.Build, WowLocale.EnUS).Columns.ToDictionary(column => column.Name);
        Assert.False(c["SpellRef"].VerifiedName);
        Assert.Equal((ColumnKind.Int, 32, false, 1), (c["SpellRef"].Kind, c["SpellRef"].Bits, c["SpellRef"].Unsigned, c["SpellRef"].ArrayLength));
        Assert.True(c["MapID"].VerifiedName);
    }

    [Fact]
    public void Carries_both_match_kinds()
    {
        var source = """
            COLUMNS
            int ID

            LAYOUT AAAA0001
            BUILD 12.0.1.60000
            $id$ID<32>
            """;
        var document = DbdDefinitionReader.Parse(Encoding.UTF8.GetBytes(source));
        var byLayout = document.SelectFile("12.0.1.60001", "AAAA0001");
        Assert.Equal("layout", byLayout.Match);
        var layout = TableSchema.FromBlock(Assert.Single(byLayout.Blocks), SchemaMatch.Layout, WowLocale.EnUS);
        var build = TableSchema.FromBlock(Assert.Single(document.SelectBuild("12.0.1.60000").Blocks), SchemaMatch.Build, WowLocale.EnUS);
        Assert.Equal(SchemaMatch.Layout, layout.Match);
        Assert.Equal(SchemaMatch.Build, build.Match);
        Assert.NotEqual(layout, build);
        Assert.Equal(build.Columns, layout.Columns);
        Assert.Throws<ArgumentOutOfRangeException>(() => TableSchema.FromBlock(byLayout.Blocks[0], (SchemaMatch)7, WowLocale.EnUS));
    }

    [Fact]
    public void Schemas_of_equal_blocks_are_equal_and_hash_alike()
    {
        var first = TableSchema.FromBlock(Block(Source), SchemaMatch.Build, WowLocale.EnUS);
        var second = TableSchema.FromBlock(Block(Source), SchemaMatch.Build, WowLocale.EnUS);
        Assert.NotSame(first, second);
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, TableSchema.FromBlock(Block(Source), SchemaMatch.Build, WowLocale.EnGB));
        Assert.NotEqual(first, TableSchema.FromBlock(Block(Source.Replace("COMMENT block note", "COMMENT other")), SchemaMatch.Build, WowLocale.EnUS));
        Assert.NotEqual(first, TableSchema.FromBlock(Block(Source.Replace("0000BEEF", "0000BEEE")), SchemaMatch.Build, WowLocale.EnUS));
        Assert.NotEqual(first, TableSchema.FromBlock(Block(Source.Replace("Small<u8>", "Small<8>")), SchemaMatch.Build, WowLocale.EnUS));
        Assert.IsAssignableFrom<IReadOnlyList<SchemaColumn>>(first.Columns);
        Assert.Throws<NotSupportedException>(() => ((IList<SchemaColumn>)first.Columns).Add(first.Columns[0]));
    }

    [Fact]
    public void Refuses_an_int_with_no_width()
    {
        var error = Refused("int Count", "Count");
        Assert.Contains("'Count'", error.Message);
        Assert.Contains("width", error.Message);
    }

    [Fact]
    public void Refuses_a_width_on_a_non_int_built_outside_the_reader()
    {
        var field = new DbdField(new DbdColumn("Scale", "float", true, null, null), 32, false, 1, false, false, false, null);
        var block = new DbdBlock([], [], [field], null);
        var error = Assert.Throws<DefinitionException>(() => TableSchema.FromBlock(block, SchemaMatch.Build, WowLocale.EnUS));
        Assert.Equal("DEFINITION_BLOCK_UNSUPPORTED", error.Code);
        Assert.Contains("'Scale'", error.Message);
        // The reader refuses the same text before the schema sees it.
        Assert.Equal("DBD_UNSUPPORTED", Assert.Throws<Db2FormatException>(() => Block(Single("float Scale", "Scale<32>"), "1.0.0.1")).Code);
    }

    [Theory]
    [InlineData("float<Map::ID> Ref", "Ref")]
    [InlineData("string<Map::ID> Ref", "Ref")]
    [InlineData("locstring<Map::ID> Ref", "Ref")]
    public void Refuses_a_foreign_key_on_a_non_int(string column, string field)
    {
        var error = Refused(column, field);
        Assert.Contains("'Ref'", error.Message);
        Assert.Contains("foreign key", error.Message);
    }

    [Theory]
    [InlineData("float ID", "$id$ID", "an id must be an int")]
    [InlineData("string ID", "$noninline,id$ID", "an id must be an int")]
    [InlineData("locstring ID", "$id$ID", "an id must be an int")]
    [InlineData("int ID", "$id$ID<32>[2]", "an id can't be an array")]
    [InlineData("int ID", "$noninline,id$ID<32>[2]", "an id can't be an array")]
    [InlineData("float ID", "$relation$ID", "a relation must be an int")]
    [InlineData("string ID", "$noninline,relation$ID", "a relation must be an int")]
    [InlineData("int ID", "$relation$ID<32>[2]", "a relation can't be an array")]
    [InlineData("int ID", "$id,relation$ID<32>", "both the id and a relation")]
    public void Refuses_an_id_or_relation_that_is_not_a_scalar_int(string column, string field, string why)
    {
        var error = Refused(column, field);
        Assert.Contains("'ID'", error.Message);
        Assert.Contains(why, error.Message);
    }

    [Fact]
    public void Refuses_a_locstring_array()
    {
        var error = Refused("locstring Name_lang", "Name_lang[2]");
        Assert.Contains("'Name_lang'", error.Message);
        Assert.Contains("locstring can't be an array", error.Message);
    }

    [Fact]
    public void Refuses_noninline_without_id_or_relation()
    {
        var error = Refused("int Parent", "$noninline$Parent<32>");
        Assert.Contains("'Parent'", error.Message);
        Assert.Contains("noninline needs id or relation", error.Message);
    }

    [Fact]
    public void One_bad_field_refuses_the_whole_block()
    {
        var source = """
            COLUMNS
            int ID
            int Count

            BUILD 1.0.0.1
            $id$ID<32>
            Count
            """;
        var error = Assert.Throws<DefinitionException>(() => TableSchema.FromBlock(Block(source, "1.0.0.1"), SchemaMatch.Layout, WowLocale.EnUS));
        Assert.Equal("DEFINITION_BLOCK_UNSUPPORTED", error.Code);
        Assert.Contains("'Count'", error.Message);
    }

    [Fact]
    public void Arrays_are_legal_on_int_float_and_string()
    {
        var c = TableSchema.FromBlock(Block(Source), SchemaMatch.Build, WowLocale.EnUS).Columns.ToDictionary(column => column.Name);
        Assert.Equal([3, 3, 2], new[] { c["Flags"].ArrayLength, c["Position"].ArrayLength, c["Paths"].ArrayLength });
    }

    [Fact]
    public void Inline_relation_stays_a_field_and_relation_key()
    {
        var schema = TableSchema.FromBlock(Block(Single("int<Parent::ID> ParentID", "$relation$ParentID<u32>"), "1.0.0.1"),
            SchemaMatch.Build, WowLocale.EnUS);
        var column = Assert.Single(schema.Columns);
        Assert.Equal((KeyStorage.Inline, KeyStorage.None, new ForeignKey("Parent", "ID")), (column.Relation, column.Id, column.ForeignKey));
    }

    [Fact]
    public void Requires_a_block_and_a_locale()
    {
        Assert.Throws<ArgumentNullException>(() => TableSchema.FromBlock(null!, SchemaMatch.Build, WowLocale.EnUS));
        Assert.Throws<ArgumentNullException>(() => TableSchema.FromBlock(Block(Source), SchemaMatch.Build, null!));
    }
}
