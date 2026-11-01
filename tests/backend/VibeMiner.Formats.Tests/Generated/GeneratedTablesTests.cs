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
    public async Task Every_identity_ingredient_changes_the_table()
    {
        var store = new InMemoryGeneratedStore();
        var tables = new GeneratedTables(store);
        await tables.StoreAsync(Source(), TactKeyStatus.None, Content(SimpleRows(3)));
        await tables.StoreAsync(Source(locale: null), TactKeyStatus.None, Content(SimpleRows(4)));

        Assert.Equal(3, (await tables.OpenAsync(Source(), TactKeyStatus.None))!.RowCount);
        // A table without locstrings has no locale: it is apart from every locale's table.
        Assert.Equal(4, (await tables.OpenAsync(Source(locale: null), TactKeyStatus.None))!.RowCount);
        foreach (var other in new[]
        {
            Source(locale: "deDE"), Source(contentKey: "10112233445566778899aabbccddeeff"), Source(definition: "def-sha256+01020305"),
        })
            Assert.Null(await tables.OpenAsync(other, TactKeyStatus.None));
        // The misses read only the start of their own chain, so the stored tables are untouched.
        Assert.Equal(3, (await tables.OpenAsync(Source(), TactKeyStatus.None))!.RowCount);
    }

    [Fact]
    public void The_identity_is_the_v2_text_with_no_build()
    {
        Assert.Equal("vibeminer-generated-table v2", GeneratedTableSource.IdentityFormat);
        Assert.Equal(string.Join('\n', "vibeminer-generated-table v2", "enUS", ContentKey, "def-sha256+01020304",
            "reader-options=" + Source().ReaderOptions, "db2-values-v3", "rows-v1"), Source().IdentityText);
        Assert.Equal(string.Join('\n', "vibeminer-generated-table v2", "-", ContentKey, "def-sha256+01020304",
            "reader-options=" + Source().ReaderOptions, "db2-values-v3", "rows-v1"), Source(locale: null).IdentityText);
        Assert.Equal(Source(), Source(limits: new Db2Limits()));
        Assert.NotEqual(Source(), Source(locale: null));
        Assert.Throws<ArgumentException>(() => Source(locale: "xxXX"));
    }

    [Fact]
    public async Task A_table_with_holes_is_filed_under_its_hole_scope_and_one_without_is_shared()
    {
        var tables = new GeneratedTables(new InMemoryGeneratedStore());
        var a = Source();
        GeneratedTableSource Scoped(string scope) => new("enUS", ContentKey, "def-sha256+01020304", new Db2Limits(), scope);
        var holed = Content(SimpleRows(1), K1) with { HoleKeys = [K1] };
        var stored = await tables.StoreAsync(Scoped("build a"), TactKeyStatus.None, holed);
        Assert.Equal(stored.Identity, tables.IdentityOf(Scoped("build a"), TactKeyStatus.None, [K1], encrypted: true));
        Assert.NotEqual(stored.Identity, tables.IdentityOf(Scoped("build a"), TactKeyStatus.None, [K1]));

        // Another build's encoding may hide other chunks: it never sees build a's partial table.
        Assert.Equal(1, (await tables.OpenAsync(Scoped("build a"), TactKeyStatus.None))!.RowCount);
        Assert.Null(await tables.OpenAsync(Scoped("build b"), TactKeyStatus.None));
        Assert.Null(await tables.OpenAsync(a, TactKeyStatus.None));

        // A table without holes is the same for every build: once b stores it, every scope reads it first, a's too.
        await tables.StoreAsync(Scoped("build b"), TactKeyStatus.None, Content(SimpleRows(3)));
        Assert.Equal(3, (await tables.OpenAsync(Scoped("build c"), TactKeyStatus.None))!.RowCount);
        Assert.Equal(3, (await tables.OpenAsync(a, TactKeyStatus.None))!.RowCount);
        Assert.Equal(3, (await tables.OpenAsync(Scoped("build a"), TactKeyStatus.None))!.RowCount);

        await Assert.ThrowsAsync<ArgumentException>(() => tables.StoreAsync(a, TactKeyStatus.None, Content(SimpleRows(1), K1) with { HoleKeys = [K1] }));
        Assert.Throws<ArgumentException>(() => tables.IdentityOf(a, TactKeyStatus.None, [K1], encrypted: true));
    }

    [Fact]
    public async Task A_table_read_through_a_decrypted_chunk_is_filed_under_its_scope_with_no_hole()
    {
        var tables = new GeneratedTables(new InMemoryGeneratedStore());
        GeneratedTableSource Scoped(string scope) => new("enUS", ContentKey, "def-sha256+01020304", new Db2Limits(), scope);
        var content = Content(SimpleRows(2)) with { KeysUsed = [K1], ChunkKeys = [K1] };
        var stored = await tables.StoreAsync(Scoped("build a"), Known(K1), content);
        Assert.Empty(stored.HoleKeys);
        Assert.Equal(VibeMiner.Formats.Tables.TableState.Ready, VibeMiner.Formats.Tables.TableStates.Generated(stored).State);
        Assert.Equal(stored.Identity, tables.IdentityOf(Scoped("build a"), Known(K1), [K1], encrypted: true));
        Assert.Null(await tables.OpenAsync(Scoped("build b"), Known(K1)));
        await Assert.ThrowsAsync<ArgumentException>(() => tables.StoreAsync(Scoped("build a"), Known(K1),
            Content(SimpleRows(1)) with { ChunkKeys = [K2] }));
    }

    [Fact]
    public async Task GetOrBuild_never_hands_one_scopes_encrypted_table_to_another()
    {
        var tables = new GeneratedTables(new InMemoryGeneratedStore());
        GeneratedTableSource Scoped(string scope) => new("enUS", ContentKey, "def-sha256+01020304", new Db2Limits(), scope);
        var release = new TaskCompletionSource();
        var builds = 0;
        // Flights are per encoding scope: b never waits on a's build, and builds its own clear table.
        var first = tables.GetOrBuildAsync(Scoped("build a"), TactKeyStatus.None, async _ =>
        {
            Interlocked.Increment(ref builds);
            await release.Task;
            return Content(SimpleRows(1), K1) with { HoleKeys = [K1] };
        });
        var second = tables.GetOrBuildAsync(Scoped("build b"), TactKeyStatus.None, _ =>
        {
            Interlocked.Increment(ref builds);
            return Task.FromResult(Content(SimpleRows(2)));
        });
        release.SetResult();
        Assert.Equal(1, (await first.WaitAsync(Guard)).RowCount);
        Assert.Equal(2, (await second.WaitAsync(Guard)).RowCount);
        Assert.Equal(2, builds);
    }

    [Fact]
    public async Task One_scopes_failed_build_is_never_another_scopes_answer()
    {
        var tables = new GeneratedTables(new InMemoryGeneratedStore());
        GeneratedTableSource Scoped(string scope) => new("enUS", ContentKey, "def-sha256+01020304", new Db2Limits(), scope);
        var release = new TaskCompletionSource();
        var first = tables.GetOrBuildAsync(Scoped("build a"), TactKeyStatus.None, async _ =>
        {
            await release.Task;
            throw new InvalidOperationException("build a's layer 4 failed");
        });
        var second = tables.GetOrBuildAsync(Scoped("build b"), TactKeyStatus.None, _ => Task.FromResult(Content(SimpleRows(2))));
        release.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.WaitAsync(Guard));
        Assert.Equal(2, (await second.WaitAsync(Guard)).RowCount);
    }

    [Fact]
    public async Task A_stored_table_records_its_files_layout_by_content_key_alone()
    {
        var store = new InMemoryGeneratedStore();
        var tables = new GeneratedTables(store);
        Assert.Null(await tables.LayoutOfAsync(ContentKey));
        // Another locale and definition of the same file: the layout is the file's, so one record answers for both.
        await tables.StoreAsync(Source(), TactKeyStatus.None, Content(SimpleRows(2)));
        await tables.StoreAsync(Source(locale: null, definition: "other"), TactKeyStatus.None, Content(SimpleRows(2)));
        Assert.Equal("01020304", await tables.LayoutOfAsync(ContentKey));
        Assert.Null(await tables.LayoutOfAsync("10112233445566778899aabbccddeeff"));

        var record = Assert.Single(await ClassifyAsync(store), k => k.Value == Kind.Layout).Key;
        using (var json = JsonDocument.Parse((await store.ReadAsync(record)).Bytes!))
            Assert.Equal(["vibeminer-generated-table-layout v1", ContentKey, "01020304"],
                json.RootElement.EnumerateObject().Select(p => p.Value.GetString()!).ToArray());
        store.Damage(record);
        Assert.Null(await tables.LayoutOfAsync(ContentKey));
        Assert.DoesNotContain(record, store.Keys);
        // The tables themselves are untouched and the next store writes the record again.
        Assert.NotNull(await tables.OpenAsync(Source(), TactKeyStatus.None));
        await tables.StoreAsync(Source(definition: "third"), TactKeyStatus.None, Content(SimpleRows(1)));
        Assert.Equal("01020304", await tables.LayoutOfAsync(ContentKey));
    }

    [Fact]
    public async Task A_layout_record_of_another_shape_or_file_reads_as_none_and_is_deleted()
    {
        var store = new InMemoryGeneratedStore();
        var tables = new GeneratedTables(store);
        await tables.StoreAsync(Source(), TactKeyStatus.None, Content(SimpleRows(1)));
        var record = Assert.Single(await ClassifyAsync(store), k => k.Value == Kind.Layout).Key;
        foreach (var bytes in new[]
        {
            "{\"Format\":\"vibeminer-generated-table-layout v1\",\"ContentKey\":\"10112233445566778899aabbccddeeff\",\"Layout\":\"01020304\"}",
            "{\"Format\":\"vibeminer-generated-table-layout v1\",\"ContentKey\":\"" + ContentKey + "\",\"Layout\":\"0102030\"}",
            "{\"Format\":\"vibeminer-generated-table-layout v2\",\"ContentKey\":\"" + ContentKey + "\",\"Layout\":\"01020304\"}",
            "not json",
        })
        {
            await store.DeleteAsync([record]);
            await store.PutAsync([new GeneratedPut(record, System.Text.Encoding.UTF8.GetBytes(bytes))]);
            Assert.Null(await tables.LayoutOfAsync(ContentKey));
            Assert.DoesNotContain(record, store.Keys);
        }
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
    public async Task A_decrypted_keyed_section_stores_and_reopens_under_its_key_and_is_not_counted_excluded()
    {
        var tables = new GeneratedTables(new InMemoryGeneratedStore());
        var rows = SimpleRows(3, "decrypted");
        var decrypted = Content(rows, K1) with
        {
            Sections = [new Db2Section(0, 3, 0, K1, true, IsDecrypted: true)],
            Completion = new Db2Completion(3, 3, 0, 0, 0, 0, "complete", 1234),
        };
        var stored = await tables.StoreAsync(Source(), Known(K1), decrypted);
        Assert.Equal([K1], stored.KeysUsed);
        Assert.True(stored.Sections.Single().IsDecrypted);
        var reopened = await tables.OpenAsync(Source(), Known(K1));
        Assert.NotNull(reopened);
        Assert.True(reopened!.Sections.Single().IsDecrypted);
        SameRows(rows, await All(reopened));
        // Filed under the key's fingerprint: without it, or with another, it isn't this table.
        Assert.Null(await tables.OpenAsync(Source(), TactKeyStatus.None));
        // A decrypted section counted as excluded, or a hidden one not counted, disagrees with the sections.
        await Assert.ThrowsAsync<ArgumentException>(() => tables.StoreAsync(Source(), Known(K1),
            Content(rows, K1) with { Sections = decrypted.Sections, Completion = decrypted.Completion with { ExcludedEncryptedSections = 1 } }));
        await Assert.ThrowsAsync<ArgumentException>(() => tables.StoreAsync(Source(), Known(K1),
            Content(rows, K1) with { Completion = decrypted.Completion }));
        // The decrypted section's key must still be among the keys used.
        await Assert.ThrowsAsync<ArgumentException>(() => tables.StoreAsync(Source(), Known(K1), decrypted with { KeysUsed = [] }));
    }

    [Fact]
    public async Task A_partial_reason_names_hidden_sections_keys_and_holes_never_a_decrypted_sections_key()
    {
        // D310: section 0 decrypted with K1, section 1 hidden under K2, and a hole named K3 that is no section's key.
        const string K3 = "3333333333333333";
        var tables = new GeneratedTables(new InMemoryGeneratedStore());
        var content = Content(SimpleRows(2, "mixed"), K1, K2, K3) with
        {
            HoleKeys = [K3],
            Sections = [new Db2Section(0, 2, 0, K1, true, IsDecrypted: true), new Db2Section(1, 3, 0, K2, true)],
            Completion = new Db2Completion(5, 2, 0, 1, 3, 0, "unencryptedSections", 1234),
        };
        var stored = await tables.StoreAsync(new("enUS", ContentKey, "def-sha256+01020304", new Db2Limits(), "build a"), Known(K1), content);
        var (state, reason, counts) = VibeMiner.Formats.Tables.TableStates.Generated(stored);
        Assert.Equal((VibeMiner.Formats.Tables.TableState.Partial, 1), (state, counts.ExcludedSections));
        Assert.Equal([K2, K3], reason!.KeyNames);
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
        await new GeneratedTables(store).StoreAsync(Source(), TactKeyStatus.None, Content(SimpleRows(2)));
        var kinds = await ClassifyAsync(store);
        var manifest = Assert.Single(kinds, k => k.Value == Kind.Manifest).Key;
        var node = Assert.Single(kinds, k => k.Value == Kind.Node).Key;

        var file = (await RecordFile.OpenAsync(store, manifest)).File!;
        using var metadata = JsonDocument.Parse(file.Metadata);
        using var nodeJson = JsonDocument.Parse((await store.ReadAsync(node)).Bytes!);
        foreach (var identity in new[] { metadata.RootElement.GetProperty("Identity"), nodeJson.RootElement.GetProperty("Identity") })
        {
            Assert.Equal("vibeminer-generated-table v2", identity.GetProperty("Format").GetString());
            Assert.Equal("enUS", identity.GetProperty("Locale").GetString());
            Assert.Equal(ContentKey, identity.GetProperty("ContentKey").GetString());
            Assert.Equal("def-sha256+01020304", identity.GetProperty("Definition").GetString());
            Assert.Equal(Source().ReaderOptions, identity.GetProperty("ReaderOptions").GetString());
            Assert.Matches("^[0-9a-f]{64}$", identity.GetProperty("ReaderOptions").GetString());
            Assert.Equal("db2-values-v3", identity.GetProperty("Reader").GetString());
            Assert.Equal("rows-v1", identity.GetProperty("Rows").GetString());
            Assert.Equal(JsonValueKind.Null, identity.GetProperty("EncodingIn").ValueKind);
            Assert.Equal(["Format", "Locale", "ContentKey", "Definition", "ReaderOptions", "Reader", "Rows", "EncodingIn"],
                identity.EnumerateObject().Select(p => p.Name).ToArray());
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
    public async Task IdentityOf_is_the_identity_the_stored_table_gets()
    {
        var store = new InMemoryGeneratedStore();
        var tables = new GeneratedTables(store);
        var plain = tables.IdentityOf(Source(), TactKeyStatus.None, []);
        Assert.Equal(0, store.Count);
        Assert.Matches("^[0-9a-f]{32}$", plain);
        Assert.Equal(plain, (await tables.StoreAsync(Source(), TactKeyStatus.None, Content(SimpleRows(2)))).Identity);

        // Unsorted, with a duplicate: filed under the same sorted distinct set.
        var keyed = tables.IdentityOf(Source(), Known(K1), [K2, K1, K2]);
        Assert.NotEqual(plain, keyed);
        Assert.Equal(keyed, (await tables.StoreAsync(Source(), Known(K1), Content(SimpleRows(2), K2, K1, K2))).Identity);
        Assert.Equal(keyed, tables.IdentityOf(Source(), Known(K1), [K1, K2]));
        Assert.NotEqual(keyed, tables.IdentityOf(Source(locale: "deDE"), Known(K1), [K1, K2]));
    }

    [Fact]
    public async Task IdentityOf_follows_a_key_status_change_as_the_stored_table_does()
    {
        var tables = new GeneratedTables(new InMemoryGeneratedStore());
        var before = tables.IdentityOf(Source(), TactKeyStatus.None, [K1]);
        Assert.Equal(before, (await tables.StoreAsync(Source(), TactKeyStatus.None, Content(SimpleRows(2), K1))).Identity);

        var known = tables.IdentityOf(Source(), Known(K1), [K1]);
        Assert.NotEqual(before, known);
        Assert.Equal(known, (await tables.StoreAsync(Source(), Known(K1), Content(SimpleRows(2), K1))).Identity);

        var refingered = new TactKeyStatus(new Dictionary<string, string> { [K1] = "fp-other" });
        var changed = tables.IdentityOf(Source(), refingered, [K1]);
        Assert.NotEqual(known, changed);
        Assert.Equal(changed, (await tables.StoreAsync(Source(), refingered, Content(SimpleRows(2), K1))).Identity);
        // A key the table doesn't use changes nothing.
        Assert.Equal(before, tables.IdentityOf(Source(), Known(K2), [K1]));
        Assert.Equal(tables.IdentityOf(Source(), TactKeyStatus.None, []), tables.IdentityOf(Source(), Known(K1), []));
    }

    [Fact]
    public void IdentityOf_refuses_bad_names_and_null_arguments()
    {
        var tables = new GeneratedTables(new InMemoryGeneratedStore());
        Assert.Throws<ArgumentException>(() => tables.IdentityOf(Source(), TactKeyStatus.None, ["111111111111111"]));
        Assert.Throws<ArgumentException>(() => tables.IdentityOf(Source(), TactKeyStatus.None, ["111111111111111g"]));
        Assert.Throws<ArgumentException>(() => tables.IdentityOf(Source(), TactKeyStatus.None, ["aaaaaaaaaaaaaaaa"]));
        Assert.ThrowsAny<ArgumentException>(() => tables.IdentityOf(Source(), TactKeyStatus.None, [null!]));
        Assert.Throws<ArgumentException>(() => tables.IdentityOf(Source(), TactKeyStatus.None,
            [.. Enumerable.Range(0, TactKeyNames.MaxKeys + 1).Select(i => i.ToString("X16", System.Globalization.CultureInfo.InvariantCulture))]));
        Assert.Throws<ArgumentNullException>(() => tables.IdentityOf(null!, TactKeyStatus.None, []));
        Assert.Throws<ArgumentNullException>(() => tables.IdentityOf(Source(), null!, []));
        Assert.Throws<ArgumentNullException>(() => tables.IdentityOf(Source(), TactKeyStatus.None, null!));
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
