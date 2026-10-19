using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using VibeMiner.Protocols.Tact;
using Xunit;
using Xunit.Abstractions;
using Harness = VibeMiner.Backend.Tests.ItemsStoreTests.Harness;

namespace VibeMiner.Backend.Tests;

[Collection("Items projection builder")]
public sealed class ItemEffectsConfigurationBudgetTests(ITestOutputHelper output)
{
    [Fact]
    public void Authentic_native_shape_is_supported_without_copying_any_downloaded_configuration()
    {
        var bytes = Dense(1757, 3518, 87836, comment: true);
        Assert.Equal(87836, bytes.Length);
        var parsed = ItemEffectsConfigurationBudget.ParseBuild(bytes, default);
        Assert.Equal(1757, parsed.Fields.Count);
        Assert.Equal(3518, parsed.Fields.Values.Sum(values => values.Count));
        Assert.Equal(5278, parsed.Fields.Count + parsed.Fields.Values.Sum(values => values.Count) + 3);
    }

    [Fact]
    public void Maximum_dense_fields_tokens_and_bytes_keep_the_parse_graph_bounded()
    {
        var bytes = Dense(2048, 6144, 131072);
        _ = ItemEffectsConfigurationBudget.ParseBuild(bytes, default); // Warm runtime metadata outside observations.
        var baseline = GC.GetTotalMemory(true); var allocated = GC.GetAllocatedBytesForCurrentThread();
        var parsed = ItemEffectsConfigurationBudget.ParseBuild(bytes, default);
        var totalAllocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        var retained = GC.GetTotalMemory(true) - baseline;
        Assert.Equal(2048, parsed.Fields.Count); Assert.Equal(8192, parsed.Fields.Count + parsed.Fields.Values.Sum(values => values.Count));
        Assert.InRange(retained, 0, 2L << 20);
        output.WriteLine("maxConfig bytes={0}; fields={1}; tokens8192; cumulativeAllocated={2}; sampledRetainedGraphDelta={3}. One parse only; not transient-live/RSS proof.", bytes.Length, parsed.Fields.Count, totalAllocated, retained);
        GC.KeepAlive(parsed);
    }

    [Fact]
    public void Maximum_line_separator_scratch_coexists_with_maximum_fields_and_tokens()
    {
        // Leading/trailing line whitespace and whitespace after '=' force both Trim copies.
        var longLine = " \tlast =\t" + new string('x', 53239) +
            string.Concat(Enumerable.Range(0, 12283).Select(index => index % 2 == 0 ? ' ' : '\t')) + "x x\t \n";
        Assert.Equal(TactConfigurationParser.MaxLineChars, longLine.Length - 1);
        var bytes = Dense(2047, 6141, 131072 - longLine.Length).Concat(Encoding.ASCII.GetBytes(longLine)).ToArray();
        _ = ItemEffectsConfigurationBudget.ParseBuild(bytes, default);
        var baseline = GC.GetTotalMemory(true); var allocated = GC.GetAllocatedBytesForCurrentThread();
        var parsed = ItemEffectsConfigurationBudget.ParseBuild(bytes, default);
        var totalAllocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        var retained = GC.GetTotalMemory(true) - baseline;
        Assert.Equal(131072, bytes.Length); Assert.Equal(2048, parsed.Fields.Count);
        Assert.Equal(8192, parsed.Fields.Count + parsed.Fields.Values.Sum(values => values.Count));
        Assert.Equal(16384, bytes.Count(value => value is (byte)' ' or (byte)'\t'));
        Assert.Equal(53239, parsed.Fields["last"][0].Length);
        Assert.Equal(["x", "x"], parsed.Fields["last"].Skip(1));
        Assert.InRange(retained, 0, 2L << 20);
        output.WriteLine("maxSeparatorConfig bytes={0}; fields2048/tokens8192/lineChars65536; cumulativeAllocated={1}; sampledRetainedGraphDelta={2}. Split scratch transient remains separately accounted.", bytes.Length, totalAllocated, retained);
        GC.KeepAlive(parsed);
    }

    [Fact]
    public void Assignment_limit_counts_compact_keys_and_rejects_one_extra_field_before_the_generic_parser()
    {
        var atLimit = Fields(ItemEffectsConfigurationBudget.MaxAssignments);
        Assert.Equal(ItemEffectsConfigurationBudget.MaxAssignments, ItemEffectsConfigurationBudget.ParseBuild(atLimit, default).Fields.Count);
        var over = Fields(ItemEffectsConfigurationBudget.MaxAssignments + 1);
        Assert.Equal(ItemEffectsConfigurationBudget.MaxAssignments + 1, TactConfigurationParser.Parse(over).Fields.Count);
        Assert.Equal("ITEM_EFFECTS_LIMIT", Limit(over).Code);
    }

    [Fact]
    public void Dense_short_tokens_charge_the_key_even_without_spaces_around_equals()
    {
        var atLimit = Tokens(ItemEffectsConfigurationBudget.MaxTokens);
        Assert.Equal(ItemEffectsConfigurationBudget.MaxTokens - 2, ItemEffectsConfigurationBudget.ParseBuild(atLimit, default).Fields.Values.Sum(values => values.Count));
        var over = Tokens(ItemEffectsConfigurationBudget.MaxTokens + 1);
        Assert.True(over.Length < ItemEffectsConfigurationBudget.MaxBuildBytes);
        Assert.Equal(ItemEffectsConfigurationBudget.MaxTokens - 1, TactConfigurationParser.Parse(over).Fields.Values.Sum(values => values.Count));
        Assert.Equal("ITEM_EFFECTS_LIMIT", Limit(over).Code);
    }

    [Fact]
    public void Raw_byte_cap_and_preflight_limits_run_before_utf8_decoding_or_splitting()
    {
        var atLimit = Dense(4, 4, ItemEffectsConfigurationBudget.MaxBuildBytes);
        Assert.Equal(4, ItemEffectsConfigurationBudget.ParseBuild(atLimit, default).Fields.Count);
        Assert.Equal("ITEM_EFFECTS_LIMIT", Limit(new byte[atLimit.Length + 1]).Code);
        Assert.Equal("ITEM_EFFECTS_LIMIT", Limit([255, .. Tokens(ItemEffectsConfigurationBudget.MaxTokens + 1)]).Code);
        Assert.Equal("ITEM_EFFECTS_LIMIT", Limit([255, .. Fields(ItemEffectsConfigurationBudget.MaxAssignments + 1)]).Code);
    }

    [Fact]
    public void Repeated_space_and_tab_separators_are_bounded_even_when_they_do_not_create_tokens()
    {
        var atLimit = Encoding.ASCII.GetBytes("key=x\n#" + new string(' ', ItemEffectsConfigurationBudget.MaxSeparators / 2) +
            new string('\t', ItemEffectsConfigurationBudget.MaxSeparators / 2));
        Assert.Equal("x", Assert.Single(ItemEffectsConfigurationBudget.ParseBuild(atLimit, default).Fields["key"]));
        var over = atLimit.Concat(new byte[] { (byte)' ' }).ToArray();
        Assert.Equal("x", Assert.Single(TactConfigurationParser.Parse(over).Fields["key"]));
        Assert.Equal("ITEM_EFFECTS_LIMIT", Limit(over).Code);
        Assert.Equal("ITEM_EFFECTS_LIMIT", Limit([255, .. over]).Code);
        var longSplit = Encoding.ASCII.GetBytes("last=x" + new string(' ', 65527) + "x x\n");
        Assert.Equal(3, TactConfigurationParser.Parse(longSplit).Fields["last"].Count);
        Assert.Equal("ITEM_EFFECTS_LIMIT", Limit(longSplit).Code);
    }

    [Fact]
    public void Comments_are_conservatively_charged_but_the_existing_parser_keeps_grammar_authority()
    {
        var allowed = Encoding.UTF8.GetBytes("# comment = counted\r\nkey\t=\tvalue one\n");
        Assert.Equal(["value", "one"], ItemEffectsConfigurationBudget.ParseBuild(allowed, default).Fields["key"]);
        Assert.Equal("ITEM_EFFECTS_LIMIT", Limit(Encoding.UTF8.GetBytes("# " + new string('=', ItemEffectsConfigurationBudget.MaxAssignments + 1))).Code);
        foreach (var invalid in new[] { "key=value\r", "key=value\nkey=other\n", "key=value=other\n", "KEY=value\n" })
        {
            var bytes = Encoding.UTF8.GetBytes(invalid);
            Assert.Equal(Assert.Throws<DiscoveryException>(() => TactConfigurationParser.Parse(bytes)).Code,
                Assert.Throws<DiscoveryException>(() => ItemEffectsConfigurationBudget.ParseBuild(bytes, default)).Code);
        }
        Assert.Equal("CONFIG_INVALID", Assert.Throws<DiscoveryException>(() =>
            ItemEffectsConfigurationBudget.ParseBuild([255], default)).Code);
        // The wider aggregate token allowance does not widen the generic per-field grammar budget.
        var tooManyOneField = Encoding.ASCII.GetBytes("key=" + string.Join(' ', Enumerable.Repeat("x", TactConfigurationParser.MaxValuesPerField + 1)));
        Assert.Equal("CONFIG_LIMIT", Assert.Throws<DiscoveryException>(() => ItemEffectsConfigurationBudget.ParseBuild(tooManyOneField, default)).Code);
    }

    [Fact]
    public void Cancellation_precedes_budget_rejection_and_parser_work()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            ItemEffectsConfigurationBudget.ParseBuild(new byte[ItemEffectsConfigurationBudget.MaxBuildBytes + 1], stop.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => ItemEffectsConfigurationBudget.ParseBuild(Tokens(ItemEffectsConfigurationBudget.MaxTokens), stop.Token));
    }

    [Fact]
    public async Task Effects_build_receipt_limit_rejects_before_attempting_the_nonexistent_retained_object()
    {
        await using var h = await Harness.Create();
        var ready = await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        var authority = await h.Catalog.ReadItemEffectsItemsAuthorityAsync(h.Opening, "enUS", ready.ArtifactId, default);
        authority = authority with { Configurations = authority.Configurations.Select(receipt => receipt.Role == "buildConfig"
            ? receipt with { ByteLength = ItemEffectsConfigurationBudget.MaxBuildBytes + 1, Sha256 = new('0', 64) } : receipt).ToArray() };
        Assert.Equal("ITEM_EFFECTS_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() => Validate(h, authority))).Code);
    }

    [Fact]
    public async Task Effects_cdn_receipt_is_raw_verified_at_one_mib_and_never_parsed_as_unused_text()
    {
        await using var h = await Harness.Create();
        var ready = await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        var authority = await h.Catalog.ReadItemEffectsItemsAuthorityAsync(h.Opening, "enUS", ready.ArtifactId, default);
        var bytes = Enumerable.Repeat((byte)255, TactConfigurationParser.MaxBytes).ToArray();
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes)); await h.Objects.PublishAsync(bytes, sha, default);
        var md5 = Convert.ToHexStringLower(MD5.HashData(bytes));
        authority = authority with { Configurations = authority.Configurations.Select(receipt => receipt.Role == "cdnConfig"
            ? receipt with { ByteLength = bytes.Length, Sha256 = sha, Key = md5 } : receipt).ToArray() };
        await Validate(h, authority);
        var forged = authority with { Configurations = authority.Configurations.Select(receipt => receipt.Role == "cdnConfig"
            ? receipt with { Key = new('0', 32) } : receipt).ToArray() };
        Assert.Equal("CONFIG_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() => Validate(h, forged))).Code);
        var oversized = authority with { Configurations = authority.Configurations.Select(receipt => receipt.Role == "cdnConfig"
            ? receipt with { ByteLength = TactConfigurationParser.MaxBytes + 1, Sha256 = new('0', 64) } : receipt).ToArray() };
        Assert.Equal("ITEM_EFFECTS_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() => Validate(h, oversized))).Code);
        Assert.True((await h.Store.AuthorizeItemEffectsAsync(new(h.Request, ready.ArtifactId, "1"), default)).Item.Admitted);
    }

    private static Task Validate(Harness h, ItemEffectsItemsAuthority authority) => (Task)typeof(ItemsStore)
        .GetMethod("ValidateEffectsSourcesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(h.Store, [h.Opening, authority, CancellationToken.None])!;
    private static byte[] Fields(int count) => Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, count).Select(i => $"key{i}=x\n")));
    private static byte[] Tokens(int count) => Encoding.ASCII.GetBytes("first=" + string.Join(' ', Enumerable.Repeat("x", (count - 2) / 2)) +
        "\nsecond=" + string.Join(' ', Enumerable.Repeat("x", count - 2 - (count - 2) / 2)) + "\n");
    // Authored shape only: no real field names, hashes, values or downloaded source bytes.
    private static byte[] Dense(int fields, int values, int byteLength, bool comment = false)
    {
        var prefix = comment ? "# authored fixture\n" : "";
        var lines = new List<string>(fields); var remaining = values;
        for (var i = 0; i < fields; i++)
        {
            var count = remaining / (fields - i); remaining -= count;
            lines.Add("k" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "=" + string.Join(' ', Enumerable.Repeat("x", count)) + "\n");
        }
        var text = string.Concat(lines); var padding = byteLength - prefix.Length - text.Length;
        var perValue = padding / values; var extra = padding % values; var result = new StringBuilder(byteLength);
        result.Append(prefix);
        foreach (var character in text)
        {
            result.Append(character);
            if (character == 'x') result.Append('x', perValue + (extra-- > 0 ? 1 : 0));
        }
        return Encoding.ASCII.GetBytes(result.ToString());
    }
    private static DiscoveryException Limit(byte[] bytes) => Assert.Throws<DiscoveryException>(() =>
        ItemEffectsConfigurationBudget.ParseBuild(bytes, default));
}
