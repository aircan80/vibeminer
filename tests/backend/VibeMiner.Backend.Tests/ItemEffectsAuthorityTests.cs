using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace VibeMiner.Backend.Tests;

public sealed class ItemEffectsAuthorityTests(ITestOutputHelper output)
{
    private const string At = "2026-10-19T00:00:00Z", Later = "2026-10-19T01:00:00Z";
    private const int Budget = 256 << 10;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { MaxDepth = 32, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false) } };

    [Fact]
    public void All_nine_call_types_ignore_only_known_receipt_verified_observations()
    {
        var item = Artifact(); var source = item.Binding.ItemSource; var association = source.Association;
        var configurations = Configurations(); var bridge = Bridge(source, configurations);
        Assert.True(Same(source.Source, source.Source with { VerifiedAt = Later }));
        Assert.True(Same(association.EncodingSource, association.EncodingSource with { VerifiedAt = Later }));
        Assert.True(Same(configurations[0], configurations[0] with { VerifiedAt = Later }));
        Assert.True(Same(association, association with { EncodingSource = association.EncodingSource with { VerifiedAt = Later }, RootSource = association.RootSource with { VerifiedAt = Later } }));
        Assert.True(Same(item.Binding.ItemRows, item.Binding.ItemRows with { }));
        var changed = item with { Binding = item.Binding with { ItemSource = Verified(source), SparseSource = Verified(item.Binding.SparseSource) } };
        Assert.True(Same(item, changed)); Assert.True(Same(Snapshot(item), Snapshot(changed)));
        Assert.True(Same<IReadOnlyList<ConfigurationReceipt>>(configurations, configurations.Select(c => c with { VerifiedAt = Later }).ToArray()));
        Assert.True(Same(bridge, bridge with { Binding = bridge.Binding with { Source = Verified(source), Configurations = configurations.Select(c => c with { VerifiedAt = Later }).ToArray() } }));
        Assert.Equal(At, source.Source.VerifiedAt); Assert.Equal(At, association.RootSource.VerifiedAt); // Inputs remain original.
    }

    [Theory]
    [InlineData("retrieved")][InlineData("url")][InlineData("prepared")][InlineData("encodingRetrieved")][InlineData("rootRetrieved")]
    public void Original_receipt_and_current_prepared_generation_poison_remain_distinct(string field)
    {
        var item = Artifact(); var source = item.Binding.ItemSource;
        var changed = field switch
        {
            "retrieved" => source with { Source = source.Source with { RetrievedAt = Later } },
            "url" => source with { Source = source.Source with { SourceUrl = "https://fixture.invalid/other" } },
            "prepared" => source with { PreparedAt = Later },
            "encodingRetrieved" => source with { Association = source.Association with { EncodingSource = source.Association.EncodingSource with { RetrievedAt = Later } } },
            _ => source with { Association = source.Association with { RootSource = source.Association.RootSource with { RetrievedAt = Later } } }
        };
        Assert.False(Same(item, item with { Binding = item.Binding with { ItemSource = changed } }));
        Assert.False(Same(Snapshot(item), Snapshot(item) with { ItemSource = changed }));
        var bridge = Bridge(source, Configurations()); Assert.False(Same(bridge, bridge with { Binding = bridge.Binding with { Source = changed } }));
        Assert.False(Same(Snapshot(item), Snapshot(item) with { PreparedAt = Later }));
    }

    [Fact]
    public void Configuration_original_receipt_and_list_order_are_strict()
    {
        var configurations = Configurations();
        Assert.False(Same<IReadOnlyList<ConfigurationReceipt>>(configurations, configurations.Reverse().ToArray()));
        Assert.False(Same(configurations[0], configurations[0] with { RetrievedAt = Later }));
        Assert.False(Same(configurations[0], configurations[0] with { SourceUrl = "https://fixture.invalid/other" }));
        var source = Artifact().Binding.ItemSource;
        var other = source.Association.Memberships[0] with { EntryOrdinal = "1" };
        var association = source.Association with { Memberships = [source.Association.Memberships[0], other] };
        Assert.False(Same(association, association with { Memberships = association.Memberships.Reverse().ToArray() }));
    }

    [Fact]
    public void Compact_comparison_accepts_dictionary_reordering_but_never_array_reordering()
    {
        var a = Encoding.UTF8.GetBytes("{\"outer\":{\"alpha\":1,\"beta\":2},\"list\":[1,2,2]}");
        var reordered = Encoding.UTF8.GetBytes("{\"list\":[1,2,2],\"outer\":{\"beta\":2,\"alpha\":1}}");
        Assert.True(Encoded(a, reordered));
        Assert.False(Encoded(a, Encoding.UTF8.GetBytes("{\"outer\":{\"alpha\":1,\"beta\":2},\"list\":[2,1,2]}")));
        Assert.False(Encoded(a, Encoding.UTF8.GetBytes("{\"outer\":{\"alpha\":1,\"beta\":2},\"list\":[1,2]}")));
        Assert.False(Encoded(Encoding.UTF8.GetBytes("{\"verifiedAt\":\"original\"}"), Encoding.UTF8.GetBytes("{\"verifiedAt\":\"replacement\"}")));
        Assert.Equal("ITEM_EFFECTS_LIMIT", Assert.Throws<DiscoveryException>(() => Encoded(new byte[Budget + 1], a)).Code);
    }

    [Fact]
    public void Configuration_collection_is_captured_once_and_oversize_refuses_before_indexing()
    {
        var values = Configurations(); var bounded = new CountedConfigurations(values, 2);
        Assert.True(Same<IReadOnlyList<ConfigurationReceipt>>(bounded, values)); Assert.Equal(1, bounded.CountReads); Assert.Equal(2, bounded.IndexReads);
        var oversized = new CountedConfigurations(values, 3);
        Assert.Equal("ITEM_EFFECTS_LIMIT", Assert.Throws<DiscoveryException>(() => Same<IReadOnlyList<ConfigurationReceipt>>(oversized, values)).Code);
        Assert.Equal(1, oversized.CountReads); Assert.Equal(0, oversized.IndexReads);
    }

    [Fact]
    public void Unsupported_generic_shape_does_not_create_new_ignored_authority_fields()
    {
        Assert.Equal("ITEMS_SOURCE_INVALID", Assert.Throws<DiscoveryException>(() => Same(new { verifiedAt = At, value = 1 }, new { verifiedAt = Later, value = 1 })).Code);
        Assert.Equal("ITEMS_SOURCE_INVALID", Assert.Throws<DiscoveryException>(() => Same<ContentReceipt>(null!, null!)).Code);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(512)]
    public void Maximal_field_graph_is_bounded_before_documents_and_avoids_per_node_DOM_allocation(int columns)
    {
        var maximum = Artifact(7 * 1024, columns);
        Assert.Equal("ITEM_EFFECTS_LIMIT", Assert.Throws<DiscoveryException>(() => Same(maximum, maximum)).Code);
        var low = 0; var high = 7 * 1024;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (JsonSerializer.SerializeToUtf8Bytes(Artifact(middle, columns), Json).Length <= Budget) low = middle; else high = middle - 1;
        }
        var first = Artifact(low, columns); var second = first with { Groups = first.Groups.ToArray() };
        var encoded = JsonSerializer.SerializeToUtf8Bytes(first, Json).Length;
        Assert.InRange(encoded, Budget - 128, Budget);
        Assert.True(Same(first, second)); // Warm serializer metadata and document pool, separately from the measured call.
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(Same(first, second));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(first, Json); var reader = new Utf8JsonReader(bytes); var tokens = 0;
        while (reader.Read()) tokens++;
        output.WriteLine($"Max admitted field fixture: {columns} columns per header/snapshot, {low} references, {encoded} encoded bytes/{tokens} tokens; warmed comparison allocated {allocated} bytes.");
        // Allocations include bounded serialization buffers and normalization. They are not a RSS/live-memory assertion.
        Assert.InRange(allocated, 1, 4 << 20);
    }

    private static bool Same<T>(T a, T b) => Invoke<bool>(typeof(DiscoveryCatalog).GetMethod("EffectsAuthority", BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(typeof(T)), [a, b]);
    private static bool Encoded(byte[] a, byte[] b) => Invoke<bool>(typeof(DiscoveryCatalog).GetMethod("EffectsEqualEncoded", BindingFlags.NonPublic | BindingFlags.Static)!, [new ReadOnlyMemory<byte>(a), new ReadOnlyMemory<byte>(b)]);
    private static T Invoke<T>(MethodInfo method, object?[] arguments)
    {
        try { return (T)method.Invoke(null, arguments)!; }
        catch (TargetInvocationException error) when (error.InnerException is not null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static TableDataSnapshot Verified(TableDataSnapshot source) => source with { Source = source.Source with { VerifiedAt = Later },
        Association = source.Association with { EncodingSource = source.Association.EncodingSource with { VerifiedAt = Later }, RootSource = source.Association.RootSource with { VerifiedAt = Later } } };
    private static ConfigurationReceipt[] Configurations() => [new("buildConfig", new('a', 32), new('b', 64), 100, "https://fixture.invalid/build", At, At, 1), new("cdnConfig", new('c', 32), new('d', 64), 100, "https://fixture.invalid/cdn", At, At, 1)];
    private static ItemsProjectionArtifact Artifact(int fields = 1, int columns = 1)
    {
        var content = new ContentReceipt(new('a', 32), new('b', 32), new('c', 64), "100", "100", "https://fixture.invalid/content", At, At, "content-blte-v1", new("loose", "0", "100", null));
        MetadataReceipt Metadata(string role) => new(role, new('d', 32), new('e', 32), new('f', 64), "100", "100", "https://fixture.invalid/metadata", At, At, "metadata-v1");
        var association = new SelectedFileAssociation("1", "enUS", "all-root-variants-consistent-asset-v1", Metadata("encoding"), Metadata("root"), [new("0", "0", new('a', 32), "2", "0", "0", "0", "0", null)]);
        var rows = new TableRowsArtifact(new('1', 64), new('2', 64), 100, new(new('3', 64), new('4', 64), new('5', 64), "provider", "enUS", "reader", new('6', 64)),
            new(5, "50238ec2", "12345678", 1, 0, 4, Enumerable.Range(0, columns).Select(i => new Db2Column("F" + i, "int", i == 0)).ToArray()), new(1, 1, 0, 0, 0, 0, "complete", 100));
        var snapshot = new TableDataSnapshot("wow", new('7', 64), "us", "50238ec2", "enUS", rows.ContentKey, At, "12345678", new('5', 64), "provider", "reader", content, association,
            Enumerable.Range(0, columns).Select(i => new TableDataColumn("F" + i, "integer", i == 0, null)).ToArray(), new("complete", "1", "1", "0", "0", "0", "0", "0", "0"), "1");
        var groups = new List<ItemPropertyGroup>();
        for (var remaining = fields; remaining > 0;) { var count = Math.Min(remaining, 1024); groups.Add(new("group" + groups.Count, Enumerable.Repeat(new ItemFieldReference("50238ec2", 0), count).ToArray())); remaining -= count; }
        return new(new('8', 64), new('9', 64), 100, new(new("wow", new('7', 64), "us", "enUS"), snapshot, snapshot with { TableId = "919be54e" }, rows, rows),
            new("1", "1", "1", "0", "0", "1", "0", "0"), groups, "items-projection-v2");
    }
    private static ItemsSnapshot Snapshot(ItemsProjectionArtifact item) => new(item.Binding.Context, item.ContentKey, At, item.FeatureRevision, "item-id-v1", item.Binding.ItemSource, item.Binding.SparseSource, item.Counts, item.Groups);
    private static ItemEffectsBridgeArtifact Bridge(TableDataSnapshot source, IReadOnlyList<ConfigurationReceipt> configurations) => new(new('a', 64), new('b', 64), 100,
        new(new("wow", new('7', 64), "us", "enUS"), "1", new("1", "us", new('a', 64), 0, new('b', 64), 0), configurations, source, Artifact().Binding.ItemRows,
            new(new("00cb674f", 0), new("00cb674f", 1), new("00cb674f", 2))), "1");
    private sealed class CountedConfigurations(ConfigurationReceipt[] values, int count) : IReadOnlyList<ConfigurationReceipt>
    {
        internal int CountReads, IndexReads;
        public int Count => ++CountReads == 1 ? count : throw new InvalidOperationException("Count must be captured once.");
        public ConfigurationReceipt this[int index] { get { IndexReads++; return values[index]; } }
        public IEnumerator<ConfigurationReceipt> GetEnumerator() => throw new InvalidOperationException("Unknown collection enumeration is forbidden.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
