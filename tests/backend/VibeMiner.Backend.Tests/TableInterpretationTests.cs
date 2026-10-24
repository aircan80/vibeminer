using System.Text;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using VibeMiner.Protocols.Tact;
using Xunit;

namespace VibeMiner.Backend.Tests;
public sealed class TableInterpretationTests
{
    [Fact]
    public void Registered_lookup_receipts_from_another_build_cannot_admit_retained_table_rows()
    {
        var f = InventoryFixture.Create(); var configuration = TactConfigurationParser.Parse(f.BuildConfig);
        var encoding = DownloadDeclarations.ReadEncoding(configuration);
        MetadataReceipt Source(string role, string ckey, string ekey, long encoded, long decoded) => new(role, ckey, ekey,
            new('a', 64), encoded.ToString(), decoded.ToString(), InventoryFixture.Url("data", ekey),
            "2026-10-19T00:00:00Z", "2026-10-19T00:00:00Z", "test");
        var en = Source("encoding", encoding.ContentKey, encoding.EncodingKey, encoding.EncodedBytes, encoding.DecodedBytes);
        var root = Source("root", configuration.Fields["root"][0], new('b', 32), 20, 10);
        var association = new SelectedFileAssociation("841626", "enUS", "enus-consistent-root-variants-v1", en, root, []);
        TableDataStore.ValidateSourceAssociation(configuration, association);
        foreach (var swapped in new[] { association with { EncodingSource = en with { ContentKey = new('c', 32) } },
            association with { EncodingSource = en with { EncodingKey = new('d', 32) } },
            association with { EncodingSource = en with { EncodedBytes = "21" } },
            association with { RootSource = root with { ContentKey = new('e', 32) } } })
            Assert.Equal("TABLE_SOURCE_CONTEXT_MISMATCH", Assert.Throws<DiscoveryException>(() => TableDataStore.ValidateSourceAssociation(configuration, swapped)).Code);
    }
    [Fact]
    public void Shared_artifact_cannot_bypass_a_later_builds_ambiguous_actual_layout()
    {
        var definition = DbdDefinitionReader.Parse(Encoding.UTF8.GetBytes("""
            COLUMNS
            int ID

            LAYOUT 01020304
            BUILD 1.2.3.4, 1.2.3.5
            $noninline,id$ID<u32>

            LAYOUT 01020304
            BUILD 1.2.3.5
            $noninline,id$ID<u32>
            """));
        Assert.Equal(TableRowArtifacts.SchemaSelectionKey(definition, "1.2.3.4"), TableRowArtifacts.SchemaSelectionKey(definition, "1.2.3.5"));
        var binding = new TableArtifactBinding(new('a', 64), new('b', 64), new('c', 64), "provider", "enUS", "reader", TableRowArtifacts.SchemaSelectionKey(definition, "1.2.3.4"));
        var artifact = new TableRowsArtifact(TableRowArtifacts.ContentKey(binding), new('d', 64), 1000, binding,
            new(5, "11223344", "01020304", 1, 0, 4, [new("ID", "integer", true)]), new(1, 1, 0, 0, 0, 0, "complete", 100));
        TableDataStore.ValidateInterpretation(artifact, definition, "1.2.3.4");
        Assert.Equal("DB2_SCHEMA_AMBIGUOUS", Assert.Throws<DiscoveryException>(() => TableDataStore.ValidateInterpretation(artifact, definition, "1.2.3.5")).Code);
        Assert.Equal("DB2_SCHEMA_MISSING", Assert.Throws<DiscoveryException>(() => TableDataStore.ValidateInterpretation(artifact with { Header = artifact.Header with { LayoutHash = "11223344" } }, definition, "1.2.3.4")).Code);
    }
    [Fact]
    public void An_unlisted_build_keys_retained_rows_by_its_layout_choice()
    {
        // D080: 1.2.3.10 is nearest the u8 block, 1.2.3.90 the u16 block; neither build is listed.
        var definition = DbdDefinitionReader.Parse(Encoding.UTF8.GetBytes("COLUMNS\nint ID\nint Value\n\nLAYOUT 01020304\nBUILD 1.2.3.4\n$noninline,id$ID<u32>\nValue<u8>\n\nLAYOUT 01020304\nBUILD 1.2.3.100\n$noninline,id$ID<u32>\nValue<u16>\n"));
        var near = TableRowArtifacts.SchemaSelectionKey(definition, "1.2.3.10");
        Assert.Equal(near, TableRowArtifacts.SchemaSelectionKey(definition, "1.2.3.11"));
        Assert.NotEqual(near, TableRowArtifacts.SchemaSelectionKey(definition, "1.2.3.90"));
        Assert.NotEqual(near, TableRowArtifacts.SchemaSelectionKey(definition, "1.2.3.4"));
        var binding = new TableArtifactBinding(new('a', 64), new('b', 64), new('c', 64), "provider", "enUS", "reader", near);
        var artifact = new TableRowsArtifact(TableRowArtifacts.ContentKey(binding), new('d', 64), 1000, binding,
            new(5, "11223344", "01020304", 1, 0, 4, [new("ID", "integer", true), new("Value", "integer", false)]), new(1, 1, 0, 0, 0, 0, "complete", 100));
        TableDataStore.ValidateInterpretation(artifact, definition, "1.2.3.10");
        Assert.Equal("DB2_SCHEMA_MISSING", Assert.Throws<DiscoveryException>(() => TableDataStore.ValidateInterpretation(artifact with { Header = artifact.Header with { LayoutHash = "11223344" } }, definition, "1.2.3.10")).Code);
        Assert.True(TableDataStore.OpensBuild(definition, "1.2.3.10", default)); Assert.False(TableDataStore.OpensBuild(definition, "2.0.0.1", default));
    }
}
