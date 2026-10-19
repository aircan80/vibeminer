using System.Buffers.Binary;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class ItemEffectsCanonicalProjectionTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ActualParserAndCanonicalProjectionAreAcceptedWithoutChangingOriginalReferences(bool name)
    {
        var provider = await BundledTableDefinitions.LoadAsync();
        var role = name ? ItemEffectsSourceRole.Name : ItemEffectsSourceRole.Effect;
        var original = ItemEffectsResultValidationTests.Source(role);
        var definition = provider.Definitions[original.TableId];
        var sink = new Sink(); using var bytes = new MemoryStream(Table(name));
        var limits = new Wdc5Limits(DefinitionSha256: definition.Sha256, VerifiedRawIntegerRules: name ? null : new[] { 3, 6, 8 }.Select(i =>
            new Wdc5RawIntegerRule(definition.Sha256, "4002A5B1", "4CA77678", i, i switch { 3 => "Charges", 6 => "SpellCategoryID", _ => "ChrSpecializationID" }, 3, 32, 16, i != 3)).ToArray());
        var completion = await Wdc5TableReader.ReadAsync(bytes, definition.Document, "12.1.5.70448", limits, sink);
        Assert.Equal(1, completion.PhysicalRows);
        var columns = sink.Header!.Columns.Select(c => TableDataStore.ProjectColumn(c, definition.Document)).ToArray();
        Assert.Equal("integer", columns[0].Family);
        var source = original with { Columns = columns, RowCount = "1", Coverage = new("complete", "1", "1", "0", "0", "0", "0", "0", "0") };
        var request = ItemEffectsResultValidationTests.Details;
        if (name)
        {
            Assert.Equal("string", columns[1].Family); Assert.Equal("0", sink.Row!.Values[0]); Assert.Equal(" ", sink.Row.Values[1]);
            var result = ItemEffectsResultValidationTests.NameResult(sink.Row.Values[1]);
            ItemEffectsValidation.Validate(result with { Sources = [result.Sources[0], new(ItemEffectsSourceRole.Name, ItemEffectsSourceState.Ready, source)] }, ItemEffectsResultValidationTests.Names);
        }
        else
        {
            Assert.Equal(new TableReference("Spell", "ID"), columns[7].Reference);
            foreach (var ordinal in new[] { 3, 6, 8 }) Assert.Null(columns[ordinal].Reference);
            var fields = ItemEffectsResultValidationTests.Fields(source).Select((f, i) => f with { Value = sink.Row!.Values[i + 1] }).ToArray();
            var result = new ItemEffectDetailsResult(request.Selection, request.BridgeIndexArtifactId, ItemEffectsValidation.InputPolicy,
                new(ItemEffectsSourceRole.Effect, ItemEffectsSourceState.Ready, source),
                [new(new("0"), sink.Row!.Id.ToString(), ItemEffectState.ObservedReady, "1", ItemEffectsResultValidationTests.Target(source, sink.Row.Id.ToString()), fields)]);
            ItemEffectsValidation.Validate(result, request);
        }
    }
    private sealed class Sink : IWdc5Consumer
    {
        internal Wdc5TableHeader? Header; internal Db2Row? Row;
        public ValueTask HeaderAsync(Wdc5TableHeader header, CancellationToken token) { Header = header; return ValueTask.CompletedTask; }
        public ValueTask RowAsync(Db2Row row, CancellationToken token) { Row = row; return ValueTask.CompletedTask; }
    }
    // Small authored WDC5 wire fixture, using the pinned real selected DBD and original parser.
    private static byte[] Table(bool name)
    {
        var count = name ? 1 : 9; var descriptor = 244; var storage = descriptor + count * 4;
        var auxiliary = storage + count * 24; var start = auxiliary + (name ? 0 : 12); var recordSize = count * 4;
        var strings = name ? 2 : 0; var bytes = new byte[start + recordSize + strings + 4]; "WDC5"u8.CopyTo(bytes); Put(4, 5);
        Put(136, 1); Put(140, (uint)count); Put(144, (uint)recordSize); Put(148, (uint)strings);
        Put(152, name ? 0x46C66698u : 0x4002A5B1u); Put(156, name ? 0x782EE721u : 0x4CA77678u);
        Half(172, 4); Put(176, (uint)count); Put(188, (uint)count * 24); Put(196, name ? 0u : 12u); Put(200, 1);
        Put(212, (uint)start); Put(216, 1); Put(220, (uint)strings); Put(228, 4);
        var palette = auxiliary;
        for (var i = 0; i < count; i++)
        {
            Half(descriptor + i * 4 + 2, i * 4); Half(storage + i * 24, i * 32);
            var raw = !name && i is 2 or 5 or 7; Half(storage + i * 24 + 2, raw ? 1 : 32);
            if (raw)
            {
                Put(storage + i * 24 + 4, 4); Put(storage + i * 24 + 8, 3); Put(storage + i * 24 + 12, (uint)i * 32); Put(storage + i * 24 + 16, 1);
                Put(palette, uint.MaxValue); palette += 4;
            }
        }
        if (name) { Put(start, 4); bytes[start + recordSize] = (byte)' '; }
        Put(start + recordSize + strings, name ? 0u : 10u); return bytes;
        void Put(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        void Half(int offset, int value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), checked((ushort)value));
    }
}
