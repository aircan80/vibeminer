using System.Text.Json;
using VibeMiner.Formats.Generated;
using VibeMiner.Generated;
using VibeMiner.Generated.RecordFiles;
using VibeMiner.Generated.Testing;
using Xunit;
using static VibeMiner.Formats.Tests.Generated.GeneratedFixtures;

namespace VibeMiner.Formats.Tests.Generated;

public sealed class GeneratedTablesTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Every_identity_ingredient_but_region_changes_the_table()
    {
        var store = new InMemoryGeneratedStore();
        var tables = new GeneratedTables(store);
        await tables.StoreAsync(Source(), TactKeyStatus.None, Content(SimpleRows(3)));

        Assert.NotNull(await tables.OpenAsync(Source(), TactKeyStatus.None));
        Assert.NotNull(await tables.OpenAsync(Source(region: "eu"), TactKeyStatus.None));
        foreach (var other in new[]
        {
            Source(product: "wowt"), Source(buildConfig: "1123456789abcdef0123456789abcdef"), Source(cdnConfig: "0edcba9876543210fedcba9876543210"),
            Source(locale: "deDE"), Source(contentKey: "10112233445566778899aabbccddeeff"), Source(definition: "def-sha256+01020305"),
        })
            Assert.Null(await tables.OpenAsync(other, TactKeyStatus.None));
        // The misses read only the start of their own chain, so the stored table is untouched.
        Assert.NotNull(await tables.OpenAsync(Source(), TactKeyStatus.None));
    }

    [Fact]
    public async Task Any_reader_option_change_misses()
    {
        var rule = new Db2RawIntegerRule(new string('a', 64), "11223344", "01020304", 1, "Value", 0, 32, 8, true);
        var tables = new GeneratedTables(new InMemoryGeneratedStore());
        var rules = new List<Db2RawIntegerRule> { rule };
        var withRule = Source(limits: new Db2Limits(VerifiedRawIntegerRules: rules));
        await tables.StoreAsync(Source(), TactKeyStatus.None, Content(SimpleRows(2)));
        await tables.StoreAsync(withRule, TactKeyStatus.None, Content(SimpleRows(3)));

        Assert.Equal(2, (await tables.OpenAsync(Source(limits: new Db2Limits()), TactKeyStatus.None))!.RowCount);
        Assert.Equal(3, (await tables.OpenAsync(Source(limits: new Db2Limits(VerifiedRawIntegerRules: [rule])), TactKeyStatus.None))!.RowCount);
        foreach (var limits in new[]
        {
            new Db2Limits(AllowRawTextEvidence: true), new Db2Limits(AllowPartialEncryptedSections: true), new Db2Limits(MaxCopyDepth: 63),
            new Db2Limits(DefinitionSha256: new string('b', 64)), new Db2Limits(VerifiedRawIntegerRules: []),
            new Db2Limits(VerifiedRawIntegerRules: [rule with { CandidateBits = 16 }]), new Db2Limits(VerifiedRawIntegerRules: [rule, rule with { FieldOrdinal = 2 }]),
        })
            Assert.Null(await tables.OpenAsync(Source(limits: limits), TactKeyStatus.None));

        // The source took a copy: changing the caller's list afterwards changes nothing.
        rules.Add(rule with { FieldOrdinal = 3 });
        Assert.Equal(3, (await tables.OpenAsync(withRule, TactKeyStatus.None))!.RowCount);
    }

    [Fact]
    public async Task Content_whose_keys_or_completion_disagree_with_its_sections_is_refused()
    {
        var tables = new GeneratedTables(new InMemoryGeneratedStore());
        var keyed = Content(SimpleRows(1), K1);
        await Assert.ThrowsAsync<ArgumentException>(() => tables.StoreAsync(Source(), TactKeyStatus.None, keyed with { KeysUsed = [K2] }));
        await Assert.ThrowsAsync<ArgumentException>(() => tables.StoreAsync(Source(), TactKeyStatus.None, keyed with { KeysUsed = [] }));
        await Assert.ThrowsAsync<ArgumentException>(() => tables.StoreAsync(Source(), TactKeyStatus.None,
            keyed with { Completion = keyed.Completion with { ExcludedEncryptedSections = 0 } }));
        var plain = Content(SimpleRows(1));
        await Assert.ThrowsAsync<ArgumentException>(() => tables.StoreAsync(Source(), TactKeyStatus.None,
            plain with { Completion = plain.Completion with { ExcludedEncryptedSections = 1 } }));
        Assert.Null(await tables.OpenAsync(Source(), TactKeyStatus.None));
        // A key used by a BLTE hole needn't have a keyed section.
        Assert.Equal([K1, K2], (await tables.StoreAsync(Source(), TactKeyStatus.None, keyed with { KeysUsed = [K2, K1] })).KeysUsed);
    }

    [Fact]
    public async Task A_caller_whose_key_arrived_during_a_shared_build_gets_its_own_table()
    {
        var tables = new GeneratedTables(new InMemoryGeneratedStore());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builds = 0;
        async Task<GeneratedTableContent> Locked(CancellationToken token)
        {
            Interlocked.Increment(ref builds);
            started.SetResult();
            await release.Task.WaitAsync(token);
            return Content(SimpleRows(1, "locked"), K1);
        }
        Task<GeneratedTableContent> Open(CancellationToken token)
        {
            Interlocked.Increment(ref builds);
            return Task.FromResult(Content(SimpleRows(6, "open"), K1));
        }
        var before = tables.GetOrBuildAsync(Source(), TactKeyStatus.None, Locked);
        await started.Task.WaitAsync(Guard);
        var after = tables.GetOrBuildAsync(Source(), Known(K1), Open);
        await Task.Yield();
        release.SetResult();

        SameRows(SimpleRows(1, "locked"), await All(await before.WaitAsync(Guard)));
        SameRows(SimpleRows(6, "open"), await All(await after.WaitAsync(Guard)));
        Assert.Equal(2, builds);
        Assert.Equal(6, (await tables.OpenAsync(Source(), Known(K1)))!.RowCount);
        Assert.Equal(1, (await tables.OpenAsync(Source(), TactKeyStatus.None))!.RowCount);
    }

    [Fact]
    public async Task Stored_node_and_metadata_echo_the_identity()
    {
        var store = new InMemoryGeneratedStore();
        await new GeneratedTables(store).StoreAsync(Source(region: "eu"), TactKeyStatus.None, Content(SimpleRows(2)));
        var kinds = await ClassifyAsync(store);
        var manifest = Assert.Single(kinds, k => k.Value == Kind.Manifest).Key;
        var node = Assert.Single(kinds, k => k.Value == Kind.Node).Key;

        var file = (await RecordFile.OpenAsync(store, manifest)).File!;
        using var metadata = JsonDocument.Parse(file.Metadata);
        using var nodeJson = JsonDocument.Parse((await store.ReadAsync(node)).Bytes!);
        foreach (var identity in new[] { metadata.RootElement.GetProperty("Identity"), nodeJson.RootElement.GetProperty("Identity") })
        {
            Assert.Equal("vibeminer-generated-table v1", identity.GetProperty("Format").GetString());
            Assert.Equal("wow", identity.GetProperty("Product").GetString());
            Assert.Equal(BuildConfig, identity.GetProperty("BuildConfig").GetString());
            Assert.Equal(CdnConfig, identity.GetProperty("CdnConfig").GetString());
            Assert.Equal("enUS", identity.GetProperty("Locale").GetString());
            Assert.Equal(ContentKey, identity.GetProperty("ContentKey").GetString());
            Assert.Equal("def-sha256+01020304", identity.GetProperty("DefinitionVersion").GetString());
            Assert.Equal(Source().ReaderOptions, identity.GetProperty("ReaderOptions").GetString());
            Assert.Matches("^[0-9a-f]{64}$", identity.GetProperty("ReaderOptions").GetString());
            Assert.Equal("db2-values-v1", identity.GetProperty("Reader").GetString());
            Assert.Equal("rows-v1", identity.GetProperty("Rows").GetString());
            Assert.DoesNotContain("eu", identity.GetRawText());
        }
        Assert.Equal("WDC5", metadata.RootElement.GetProperty("Header").GetProperty("Format").GetString());
        Assert.Equal(0, nodeJson.RootElement.GetProperty("Keys").GetArrayLength());
    }

    [Fact]
    public async Task A_late_key_misses_only_the_table_that_uses_it()
    {
        var store = new InMemoryGeneratedStore();
        var tables = new GeneratedTables(store);
        var a = Source(contentKey: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var b = Source(contentKey: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var c = Source(contentKey: "cccccccccccccccccccccccccccccccc");
        await tables.StoreAsync(a, TactKeyStatus.None, Content(SimpleRows(3, "a")));
        await tables.StoreAsync(b, TactKeyStatus.None, Content(SimpleRows(2, "b-locked"), K1));
        await tables.StoreAsync(c, TactKeyStatus.None, Content(SimpleRows(4, "c"), K2));

        var k1 = Known(K1);
        var puts = store.Puts;
        var openA = await tables.OpenAsync(a, k1);
        var openC = await tables.OpenAsync(c, k1);
        Assert.Null(await tables.OpenAsync(b, k1));
        Assert.Equal(puts, store.Puts);
        SameRows(SimpleRows(3, "a"), await All(openA!));
        SameRows(SimpleRows(4, "c"), await All(openC!));

        var rebuilt = await tables.StoreAsync(b, k1, Content(SimpleRows(5, "b-open"), K1));
        SameRows(SimpleRows(5, "b-open"), await All(rebuilt));

        puts = store.Puts;
        Assert.Equal(3, (await tables.OpenAsync(a, k1))!.RowCount);
        Assert.Equal(5, (await tables.OpenAsync(b, k1))!.RowCount);
        Assert.Equal(4, (await tables.OpenAsync(c, k1))!.RowCount);
        // The table stored while K1 was unknown still answers for that status.
        Assert.Equal(2, (await tables.OpenAsync(b, TactKeyStatus.None))!.RowCount);
        Assert.Equal(puts, store.Puts);
    }

    [Fact]
    public async Task A_growing_key_set_resolves_at_every_step()
    {
        var store = new InMemoryGeneratedStore();
        var tables = new GeneratedTables(store);
        var source = Source();

        await tables.StoreAsync(source, TactKeyStatus.None, Content(SimpleRows(1, "none"), K1));
        Assert.Equal([K1], (await tables.OpenAsync(source, TactKeyStatus.None))!.KeysUsed);

        // K1 opens a section keyed with K2: the table now depends on both.
        var k1 = Known(K1);
        Assert.Null(await tables.OpenAsync(source, k1));
        await tables.StoreAsync(source, k1, Content(SimpleRows(2, "k1"), K1, K2));
        var step2 = await tables.OpenAsync(source, k1);
        Assert.Equal([K1, K2], step2!.KeysUsed);
        SameRows(SimpleRows(2, "k1"), await All(step2));

        var both = Known(K1, K2);
        Assert.Null(await tables.OpenAsync(source, both));
        await tables.StoreAsync(source, both, Content(SimpleRows(3, "both"), K1, K2));
        var step3 = await tables.OpenAsync(source, both);
        Assert.Equal([K1, K2], step3!.KeysUsed);
        SameRows(SimpleRows(3, "both"), await All(step3));

        // Each earlier status still resolves to its own table.
        Assert.Equal(1, (await tables.OpenAsync(source, TactKeyStatus.None))!.RowCount);
        Assert.Equal(2, (await tables.OpenAsync(source, k1))!.RowCount);
        // A different fingerprint for a known key is a different status.
        Assert.Null(await tables.OpenAsync(source, new TactKeyStatus(new Dictionary<string, string> { [K1] = "other", [K2] = "fp-" + K2 })));
    }

    [Fact]
    public async Task Storing_twice_is_idempotent()
    {
        var store = new InMemoryGeneratedStore();
        var tables = new GeneratedTables(store);
        await tables.StoreAsync(Source(), TactKeyStatus.None, Content(SimpleRows(10)));
        var objects = store.Count;
        var again = await tables.StoreAsync(Source(), TactKeyStatus.None, Content(SimpleRows(10)));
        Assert.Equal(objects, store.Count);
        SameRows(SimpleRows(10), await All(again));
    }

    [Theory]
    [InlineData(Kind.Node)]
    [InlineData(Kind.Manifest)]
    [InlineData(Kind.Segment)]
    public async Task A_damaged_piece_reads_as_a_miss_is_deleted_and_rebuilds(Kind kind)
    {
        var store = new InMemoryGeneratedStore();
        var tables = new GeneratedTables(store, new RecordFileOptions { BlockBytes = 64, SegmentBytes = 512 });
        var rows = SimpleRows(200);
        await tables.StoreAsync(Source(), TactKeyStatus.None, Content(rows));
        var damaged = (await ClassifyAsync(store)).Where(k => k.Value == kind).Select(k => k.Key).Last();
        store.Damage(damaged);

        Assert.Null(await tables.OpenAsync(Source(), TactKeyStatus.None));
        Assert.DoesNotContain(damaged, store.Keys);

        SameRows(rows, await All(await tables.StoreAsync(Source(), TactKeyStatus.None, Content(rows))));
        SameRows(rows, await All((await tables.OpenAsync(Source(), TactKeyStatus.None))!));
    }

    [Fact]
    public async Task Damage_found_while_reading_throws_and_the_next_open_misses()
    {
        var store = new InMemoryGeneratedStore();
        var tables = new GeneratedTables(store, new RecordFileOptions { BlockBytes = 64, SegmentBytes = 512 });
        var rows = SimpleRows(200);
        var table = await tables.StoreAsync(Source(), TactKeyStatus.None, Content(rows));
        var segment = (await ClassifyAsync(store)).Where(k => k.Value == Kind.Segment).Select(k => k.Key).Last();
        store.Damage(segment);

        var error = await Assert.ThrowsAsync<GeneratedStoreException>(() => All(table));
        Assert.Equal(GeneratedStoreCodes.Corrupt, error.Code);
        Assert.DoesNotContain(segment, store.Keys);
        Assert.Null(await tables.OpenAsync(Source(), TactKeyStatus.None));
        SameRows(rows, await All(await tables.StoreAsync(Source(), TactKeyStatus.None, Content(rows))));
    }

    [Fact]
    public async Task A_healthy_shared_segment_survives_another_tables_damaged_node()
    {
        // Two locales with the same rows share content-addressed segments; damage to one's node must not delete them.
        var store = new InMemoryGeneratedStore();
        var tables = new GeneratedTables(store);
        await tables.StoreAsync(Source(locale: "enUS"), TactKeyStatus.None, Content(SimpleRows(50)));
        await tables.StoreAsync(Source(locale: "deDE"), TactKeyStatus.None, Content(SimpleRows(50)));
        var before = await ClassifyAsync(store);
        var segments = before.Where(k => k.Value == Kind.Segment).Select(k => k.Key).ToArray();
        foreach (var node in before.Where(k => k.Value == Kind.Node).Select(k => k.Key).Take(1)) store.Damage(node);

        var misses = 0;
        foreach (var locale in new[] { "enUS", "deDE" })
            if (await tables.OpenAsync(Source(locale: locale), TactKeyStatus.None) is { } t) SameRows(SimpleRows(50), await All(t)); else misses++;
        Assert.Equal(1, misses);
        Assert.All(segments, s => Assert.Contains(s, store.Keys));
    }

    [Fact]
    public async Task Concurrent_callers_share_one_build()
    {
        var tables = new GeneratedTables(new InMemoryGeneratedStore());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builds = 0;
        async Task<GeneratedTableContent> Build(CancellationToken token)
        {
            Interlocked.Increment(ref builds);
            await release.Task.WaitAsync(token);
            return Content(SimpleRows(7));
        }
        var first = tables.GetOrBuildAsync(Source(), TactKeyStatus.None, Build);
        var second = tables.GetOrBuildAsync(Source(), TactKeyStatus.None, Build);
        release.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(Guard);
        Assert.Equal(1, builds);
        foreach (var table in results) SameRows(SimpleRows(7), await All(table));

        // Once stored, a later caller opens it without building.
        await tables.GetOrBuildAsync(Source(), TactKeyStatus.None, Build).WaitAsync(Guard);
        Assert.Equal(1, builds);
    }

    [Fact]
    public async Task One_canceled_caller_leaves_the_shared_build_running_for_the_other()
    {
        var tables = new GeneratedTables(new InMemoryGeneratedStore());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builds = 0;
        CancellationToken buildToken = default;
        async Task<GeneratedTableContent> Build(CancellationToken token)
        {
            Interlocked.Increment(ref builds);
            buildToken = token;
            started.SetResult();
            await release.Task.WaitAsync(token);
            return Content(SimpleRows(4));
        }
        using var cancel = new CancellationTokenSource();
        var leaving = tables.GetOrBuildAsync(Source(), TactKeyStatus.None, Build, cancel.Token);
        await started.Task.WaitAsync(Guard);
        var staying = tables.GetOrBuildAsync(Source(), TactKeyStatus.None, Build);
        await Task.Yield();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaving.WaitAsync(Guard));
        Assert.False(buildToken.IsCancellationRequested);
        release.SetResult();
        SameRows(SimpleRows(4), await All(await staying.WaitAsync(Guard)));
        Assert.Equal(1, builds);
    }

    [Fact]
    public async Task The_build_is_canceled_once_every_caller_has_left()
    {
        var tables = new GeneratedTables(new InMemoryGeneratedStore());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<GeneratedTableContent> Build(CancellationToken token)
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { stopped.SetResult(); }
            throw new InvalidOperationException("unreachable");
        }
        using var cancel = new CancellationTokenSource();
        var only = tables.GetOrBuildAsync(Source(), TactKeyStatus.None, Build, cancel.Token);
        await started.Task.WaitAsync(Guard);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => only.WaitAsync(Guard));
        await stopped.Task.WaitAsync(Guard);

        // A new caller starts a fresh build.
        var table = await tables.GetOrBuildAsync(Source(), TactKeyStatus.None, _ => Task.FromResult(Content(SimpleRows(2)))).WaitAsync(Guard);
        Assert.Equal(2, table.RowCount);
    }

    [Fact]
    public async Task A_failed_build_reaches_every_waiter_and_a_later_call_retries()
    {
        var tables = new GeneratedTables(new InMemoryGeneratedStore());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<GeneratedTableContent> Fail(CancellationToken token)
        {
            await release.Task.WaitAsync(token);
            throw new InvalidOperationException("authored build failure");
        }
        var first = tables.GetOrBuildAsync(Source(), TactKeyStatus.None, Fail);
        var second = tables.GetOrBuildAsync(Source(), TactKeyStatus.None, Fail);
        release.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.WaitAsync(Guard));
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.WaitAsync(Guard));
        var table = await tables.GetOrBuildAsync(Source(), TactKeyStatus.None, _ => Task.FromResult(Content(SimpleRows(1)))).WaitAsync(Guard);
        Assert.Equal(1, table.RowCount);
    }
}
