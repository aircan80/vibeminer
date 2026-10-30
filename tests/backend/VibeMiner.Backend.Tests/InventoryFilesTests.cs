using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Host;
using VibeMiner.Infrastructure;
using Xunit;
namespace VibeMiner.Backend.Tests;

// Files (D068): one row per ROOT FileDataID or install name, bounded filtered pages and the per-file inspector.
public sealed class InventoryFilesTests
{
    private const string Build = "1111111111111111111111111111111111111111111111111111111111111111";
    private const uint AllLocales = 0x1F3F6, NoNames = 0x10000000, Encrypted = 0x08000000;
    private const string SharedKey = "aaaaaaaa000000000000000000000000";

    [Fact]
    public async Task A_prepared_inventory_lists_one_row_per_FileDataID_and_install_name_with_every_membership_in_the_inspector()
    {
        await using var h = await Prepared.Create();
        var snapshot = await h.Open(); var g = snapshot.Inventory!.Generation;
        var page = await h.Catalog.ReadInventoryFilesAsync(new("wow", snapshot.Build.Id, "us", g, new()), default);
        Assert.Equal(["root:1001", "root:1002", $"install:{InstallId(page)}"], page.Entries.Select(e => e.Key));
        Assert.Equal("3", page.TotalCount); Assert.Null(page.NextCursor); Assert.False(page.ScanLimited);
        var first = page.Entries[0];
        Assert.Equal(("root", "1001", (string?)null, 2, 2, 1, 0, 0), (first.Family, first.FileDataId, first.Name, first.MembershipCount, first.MatchingCount, first.ContentKeyCount, first.UnresolvedCount, first.StoredCount));
        Assert.Equal(h.Fixture.PayloadA.Encoded.Length.ToString(CultureInfo.InvariantCulture), first.EncodedBytesMin);
        Assert.Equal(first.EncodedBytesMin, first.EncodedBytesMax);
        Assert.Equal("3", first.LocaleFlags);
        var install = page.Entries[2];
        Assert.Equal(("install", (string?)null, "authored/data-a.bin", (string?)null), (install.Family, install.FileDataId, install.Name, install.LocaleFlags));

        var detail = await h.Catalog.ReadInventoryFileAsync(new("wow", snapshot.Build.Id, "us", g, "root:1001", new(LocaleFlags: "2")), default);
        Assert.Equal((2, 1), (detail.MembershipCount, detail.MatchingCount)); Assert.Null(detail.NextCursor);
        var (named, unnamed) = (detail.Memberships[0], detail.Memberships[1]);
        Assert.Equal((0, 0, "1", "0", false), (named.BlockOrdinal, named.EntryOrdinal, named.LocaleFlags, named.ContentFlags, named.Matches));
        Assert.Equal(new InventoryRawContentFlags("0", "0", 0), named.RawContentFlags);
        Assert.Equal(0x123456789abcdef0UL.ToString(CultureInfo.InvariantCulture), named.NameHash);
        Assert.Equal((1, 0, "2", NoNames.ToString(CultureInfo.InvariantCulture), (string?)null, true), (unnamed.BlockOrdinal, unnamed.EntryOrdinal, unnamed.LocaleFlags, unnamed.ContentFlags, unnamed.NameHash, unnamed.Matches));
        Assert.All(detail.Memberships, m => { Assert.Equal(h.Fixture.PayloadA.ContentKey, m.ContentKey); Assert.Equal(h.Fixture.PayloadA.EncodingKey, m.EncodingKey); Assert.False(m.Stored); });
        Assert.Equal(h.Fixture.PayloadA.Decoded.Length.ToString(CultureInfo.InvariantCulture), named.DecodedBytes);
        var installed = await h.Catalog.ReadInventoryFileAsync(new("wow", snapshot.Build.Id, "us", g, install.Key, new()), default);
        Assert.Equal("authored/data-a.bin", installed.Name); var member = Assert.Single(installed.Memberships);
        Assert.Equal(("install", (int?)null, 0, (string?)null, (InventoryRawContentFlags?)null), (member.Family, member.BlockOrdinal, member.InstallOrdinal, member.LocaleFlags, member.RawContentFlags));

        // Filters test single memberships: NoNames is set only on 1001's second block; install has no ROOT flags.
        async Task<string[]> Keys(InventoryFileFilter filter) => (await h.Catalog.ReadInventoryFilesAsync(new("wow", snapshot.Build.Id, "us", g, filter), default)).Entries.Select(e => e.Key).ToArray();
        Assert.Equal(["root:1001"], await Keys(new(ContentFlags: NoNames.ToString(CultureInfo.InvariantCulture))));
        Assert.Equal(["root:1001", "root:1002"], await Keys(new(ContentFlags: "none")));
        Assert.Equal(["root:1001"], await Keys(new(LocaleFlags: "2")));
        Assert.Equal(["root:1001", install.Key], await Keys(new(ContentKey: h.Fixture.PayloadA.ContentKey[..6])));
        Assert.Equal([install.Key], await Keys(new(Family: "install")));
        Assert.Empty(await Keys(new(StoredOnly: true)));
        Assert.Empty(await Keys(new(FileDataId: "1003")));
        Assert.Equal("INVENTORY_FILE_NOT_FOUND", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReadInventoryFileAsync(new("wow", snapshot.Build.Id, "us", g, "root:1003", new()), default))).Code);
        Assert.Equal("INVENTORY_NOT_FOUND", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReadInventoryFilesAsync(new("wow", snapshot.Build.Id, "us", "9", new()), default))).Code);
    }

    // The Files columns are derived and outside the digest; every reopen checks each against its digest-covered source.
    [Theory]
    [InlineData("UPDATE inventory_origins SET fdid_n=1003 WHERE fdid='1002'")]
    [InlineData("UPDATE inventory_origins SET fdid_n=NULL WHERE fdid='1002'")]
    [InlineData("UPDATE inventory_origins SET locale_n=locale_n|512 WHERE family='root' AND fdid='1002'")]
    [InlineData("UPDATE inventory_origins SET flags_n=4 WHERE family='root' AND fdid='1002'")]
    [InlineData("UPDATE inventory_origins SET grp=grp+1 WHERE family='install'")]
    [InlineData("UPDATE inventory_origins SET grp=NULL WHERE family='install'")]
    [InlineData("UPDATE inventory_origins SET grp=1 WHERE family='root' AND fdid='1002'")]
    [InlineData("UPDATE inventory_origins SET fdid_n=7 WHERE family='install'")]
    [InlineData("UPDATE inventory_origins SET locale_n=2 WHERE family='download'")]
    public async Task Reopening_refuses_Files_columns_that_disagree_with_their_source_columns(string tamper)
    {
        await using var h = await Prepared.Create();
        await h.Open();
        await h.Execute(tamper);
        h.Handler.BlockAll = true;
        Assert.Equal("CHECKPOINT_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Open())).Code);
    }

    // stored and the planner statistics are rebuilt from the checked columns at every seal, so tampering with them
    // cannot survive a reopen: the next page matches the untampered one.
    [Fact]
    public async Task Reopening_rebuilds_the_stored_marks_and_planner_statistics()
    {
        await using var h = await Prepared.Create();
        var before = await h.Open(); var g = before.Inventory!.Generation;
        async Task<InventoryFilesPage> Page(InventoryFileFilter filter) => await h.Catalog.ReadInventoryFilesAsync(new("wow", before.Build.Id, "us", g, filter), default);
        var all = await Page(new()); var locale = await Page(new(LocaleFlags: "2"));
        await h.Execute($"UPDATE inventory_origins SET stored=1 WHERE g={g}; UPDATE inventory_root_variants SET members=0 WHERE g={g}; " +
            $"UPDATE inventory_file_stats SET root_files=999,install_files=0,stored_root_members=5,variants=1 WHERE g={g}");
        Assert.Empty((await Page(new(LocaleFlags: "2"))).Entries); // the tampered statistics do mislead the planner until reopen
        h.Handler.BlockAll = true;
        await h.Open();
        Assert.Equal(all.Entries, (await Page(new())).Entries); Assert.Equal("3", (await Page(new())).TotalCount);
        Assert.Equal(locale.Entries, (await Page(new(LocaleFlags: "2"))).Entries);
        Assert.Empty((await Page(new(StoredOnly: true))).Entries);
        Assert.Equal(0L, await h.Scalar($"SELECT count(*) FROM inventory_origins WHERE g={g} AND stored=1"));
    }

    [Fact]
    public async Task A_version_16_catalog_with_a_prepared_inventory_upgrades_and_lists_its_files()
    {
        await using var h = await Prepared.Create();
        var before = await h.Open(); var g = before.Inventory!.Generation;
        var expected = (await h.Catalog.ReadInventoryFilesAsync(new("wow", before.Build.Id, "us", g, new()), default)).Entries;
        // The catalog as #5's engine leaves it: no Files columns, indexes or statistics, version 16.
        await h.Restart(CatalogMigrationFixture.RemoveV17 + "PRAGMA user_version=16;");
        Assert.Equal(22L, await h.Scalar("PRAGMA user_version"));
        Assert.Equal(0L, await h.Scalar($"SELECT count(*) FROM inventory_origins WHERE g={g} AND family='root' AND fdid_n IS NOT CAST(fdid AS INTEGER)"));
        Assert.Equal(0L, await h.Scalar($"SELECT count(*) FROM inventory_origins WHERE g={g} AND family='install' AND grp IS NULL"));
        // The saved digest still verifies, and the reseal records the Files statistics.
        h.Handler.BlockAll = true;
        var after = await h.Open(); Assert.Equal(g, after.Inventory!.Generation);
        var page = await h.Catalog.ReadInventoryFilesAsync(new("wow", after.Build.Id, "us", g, new()), default);
        Assert.Equal(expected, page.Entries);
        Assert.Equal(2, (await h.Catalog.ReadInventoryFileAsync(new("wow", after.Build.Id, "us", g, "root:1001", new()), default)).MembershipCount);
    }

    [Fact]
    public async Task A_version_17_catalog_with_a_prepared_inventory_upgrades_and_Files_keeps_reading_that_generation()
    {
        await using var h = await Prepared.Create();
        var before = await h.Open(); var g = before.Inventory!.Generation;
        var expected = (await h.Catalog.ReadInventoryFilesAsync(new("wow", before.Build.Id, "us", g, new()), default)).Entries;
        // The catalog as #20's engine leaves it: Files columns and statistics, no file catalogs table, version 17.
        await h.Restart(CatalogMigrationFixture.RemoveV18 + "PRAGMA user_version=17;");
        Assert.Equal(22L, await h.Scalar("PRAGMA user_version"));
        Assert.Equal(0L, await h.Scalar("SELECT count(*) FROM file_catalogs"));
        // The legacy full catalog is still the one generation its workspace names, and nothing queues a second one for it (D071).
        Assert.Equal(expected, (await h.Catalog.ReadInventoryFilesAsync(new("wow", before.Build.Id, "us", g, new()), default)).Entries);
        h.Handler.BlockAll = true;
        var after = await h.Open(); Assert.Equal(g, after.Inventory!.Generation);
        Assert.Equal(expected, (await h.Catalog.ReadInventoryFilesAsync(new("wow", after.Build.Id, "us", g, new()), default)).Entries);
        Assert.Equal(0L, await h.Scalar("SELECT count(*) FROM file_catalogs"));
        Assert.Equal("notOpened", (await h.Catalog.ReadFileCatalogAsync(new("wow", after.Build.Id, "eu"), default)).State);
    }

    public static TheoryData<string> Filters => new()
    {
        "", "family=root", "family=install", "fdid=301", "fdid=302", "ckey=" + SharedKey, "ckey=bbbb", "ckey=dddd0001",
        "locale=2", "locale=512", "locale=8", "flags=none", "flags=4", "flags=" + Encrypted, "stored",
        "stored flags=4", "stored flags=32", "stored locale=512 flags=" + Encrypted, "stored ckey=" + SharedKey, "family=install stored",
    };

    [Theory]
    [MemberData(nameof(Filters))]
    public async Task Every_driver_and_budget_cursor_returns_exactly_the_files_a_full_scan_finds(string text)
    {
        await using var seeded = await Seeded.Create(Rows());
        var filter = Filter(text); var query = InventoryService.Parse(filter);
        foreach (var budget in new[] { 65_536, 64 })
        {
            seeded.Catalog.FilesExaminationBudget = budget;
            var keys = new List<string>(); string? after = null; var limited = 0;
            for (var pages = 0; ; pages++)
            {
                Assert.True(pages < 5_000, "Files paging did not terminate.");
                var page = await seeded.Catalog.ReadInventoryFilesAsync(new("wow", Build, "us", "1", filter, 7, after), default);
                Assert.InRange(page.Entries.Count, 0, 7);
                if (page.ScanLimited) { limited++; Assert.NotNull(page.NextCursor); }
                Assert.Equal(text is "" or "family=root" or "family=install" ? Expected(seeded.Rows, query).Count.ToString(CultureInfo.InvariantCulture) : null, page.TotalCount);
                foreach (var entry in page.Entries)
                {
                    var members = seeded.Rows.Where(r => Key(r) == entry.Key).ToArray();
                    Assert.Equal(members.Length, entry.MembershipCount);
                    Assert.Equal(members.Count(r => Matches(query, r)), entry.MatchingCount);
                    Assert.Equal(members.Count(r => r.Stored), entry.StoredCount);
                    Assert.Equal(members.Select(r => r.CKey).Distinct().Count(), entry.ContentKeyCount);
                }
                keys.AddRange(page.Entries.Select(e => e.Key));
                Assert.InRange(seeded.Catalog.LastFilesRowsRead, 0, budget + DiscoveryCatalog.FilesReadSlack);
                if (page.NextCursor is null) break;
                Assert.NotEqual(after, page.NextCursor); after = page.NextCursor;
            }
            Assert.Equal(Expected(seeded.Rows, query), keys);
            // The shared CKey has about 300 memberships and few stored ones: its index stream ends pages at the budget.
            if (budget == 64 && text == "stored ckey=" + SharedKey) Assert.True(limited > 0, "A sparse match inside a driving index should return a scan-limited page.");
        }
    }

    [Fact]
    public async Task A_sparse_filter_ends_a_page_at_the_examination_budget_instead_of_scanning_the_catalog()
    {
        await using var seeded = await Seeded.Create(Rows());
        seeded.Catalog.FilesExaminationBudget = 64;
        // No membership has locale bit 0x8: the variant statistics prove an empty result without reading a row.
        var absent = await seeded.Catalog.ReadInventoryFilesAsync(new("wow", Build, "us", "1", new(LocaleFlags: "8")), default);
        Assert.Empty(absent.Entries); Assert.Null(absent.NextCursor); Assert.False(absent.ScanLimited);
        // Only the last FileDataID carries locale bit 0x8000000; its variant stream reaches it without walking the catalog.
        var rare = await seeded.Catalog.ReadInventoryFilesAsync(new("wow", Build, "us", "1", new(LocaleFlags: "134217728")), default);
        Assert.Equal("root:12003", Assert.Single(rare.Entries).Key); Assert.Null(rare.NextCursor); Assert.False(rare.ScanLimited);
        var scan = await seeded.Catalog.ReadInventoryFilesAsync(new("wow", Build, "us", "1", new(LocaleFlags: "512", ContentFlags: Encrypted.ToString(CultureInfo.InvariantCulture)), 50), default);
        Assert.True(scan.ScanLimited); Assert.NotNull(scan.NextCursor); Assert.InRange(scan.Entries.Count, 0, 49);
    }

    [Fact]
    public async Task A_file_at_the_membership_bound_reads_within_the_stated_limits_and_counts_every_content_key()
    {
        const int max = DiscoveryCatalog.MaxFileMemberships;
        // FileDataID 5 holds the maximum: every membership has its own CKey, and only the last one is stored.
        var rows = Enumerable.Range(0, max).Select(i => new Row(i + 1, "root", 5, null, i.ToString("x32"), AllLocales, NoNames | (i % 2 == 0 ? 0x80u : 0), i == max - 1, 100 + i)).ToList();
        rows.AddRange(Enumerable.Range(0, 200).Select(i => new Row(max + 1 + i, "root", 10 + i, null, (max + i).ToString("x32"), AllLocales, NoNames, i % 7 == 0, 10)));
        await using var seeded = await Seeded.Create(rows);
        seeded.Catalog.FilesExaminationBudget = 64;
        var all = new List<string>(); string? after = null;
        do
        {
            var page = await seeded.Catalog.ReadInventoryFilesAsync(new("wow", Build, "us", "1", new(StoredOnly: true, ContentFlags: "128"), 50, after), default);
            Assert.InRange(seeded.Catalog.LastFilesRowsRead, 0, 64 + DiscoveryCatalog.FilesReadSlack);
            all.AddRange(page.Entries.Select(e => e.Key)); after = page.NextCursor;
        } while (after is not null);
        Assert.Empty(all); // the stored membership of FileDataID 5 has no 0x80
        var big = Assert.Single((await seeded.Catalog.ReadInventoryFilesAsync(new("wow", Build, "us", "1", new(FileDataId: "5")), default)).Entries);
        Assert.Equal((max, max, max, 1), (big.MembershipCount, big.MatchingCount, big.ContentKeyCount, big.StoredCount));
        var inspected = new List<InventoryFileMembership>(); string? cursor = null;
        do
        {
            var detail = await seeded.Catalog.ReadInventoryFileAsync(new("wow", Build, "us", "1", "root:5", new(ContentFlags: "128"), 100, cursor), default);
            Assert.Equal((max, max / 2), (detail.MembershipCount, detail.MatchingCount));
            Assert.InRange(seeded.Catalog.LastFileRowsRead, 1, max + 101);
            inspected.AddRange(detail.Memberships); cursor = detail.NextCursor;
        } while (cursor is not null);
        Assert.Equal(max, inspected.Select(m => m.Id).Distinct().Count()); Assert.Equal(max / 2, inspected.Count(m => m.Matches));
    }

    [Theory]
    [InlineData("root")] [InlineData("install")]
    public async Task Sealing_refuses_a_file_with_more_memberships_than_Files_reads_per_file(string family)
    {
        var rows = Enumerable.Range(0, DiscoveryCatalog.MaxFileMemberships + 1).Select(i => new Row(i + 1, family, family == "root" ? 5 : null, family == "root" ? null : "authored/huge.bin", i.ToString("x32"), AllLocales, NoNames, false, 1)).ToList();
        Assert.Equal("INVENTORY_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() => Seeded.Create(rows))).Code);
    }

    [Fact]
    public async Task A_content_key_prefix_that_matches_too_many_memberships_is_refused_as_too_broad()
    {
        var rows = Enumerable.Range(0, DiscoveryCatalog.ContentPrefixBound + 1).Select(i => new Row(i + 1, "root", i, null, "cccc" + i.ToString("x28"), AllLocales, NoNames, false, 100)).ToList();
        await using var seeded = await Seeded.Create(rows);
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => seeded.Catalog.ReadInventoryFilesAsync(new("wow", Build, "us", "1", new(ContentKey: "cccc")), default));
        Assert.Equal("INVENTORY_FILTER_BROAD", error.Code);
        var exact = await seeded.Catalog.ReadInventoryFilesAsync(new("wow", Build, "us", "1", new(ContentKey: "cccc" + 5.ToString("x28"))), default);
        Assert.Equal("root:5", Assert.Single(exact.Entries).Key);
    }

    [Fact]
    public async Task The_inspector_pages_memberships_marks_filter_matches_and_keeps_unresolved_sizes_unknown()
    {
        var rows = new List<Row>
        {
            new(1, "root", 7, null, SharedKey, 0x2, NoNames, true, 900), new(2, "root", 7, null, SharedKey, 0x200, NoNames, false, null),
            new(3, "root", 7, null, "bbbb0000000000000000000000000001", 0x2, NoNames | Encrypted, false, 1200),
            new(4, "install", null, "authored/same.bin", SharedKey, 0, 0, false, 900), new(5, "install", null, "authored/other.bin", SharedKey, 0, 0, true, 900),
            new(6, "install", null, "authored/same.bin", "bbbb0000000000000000000000000002", 0, 0, true, 50),
        };
        await using var seeded = await Seeded.Create(rows);
        var files = await seeded.Catalog.ReadInventoryFilesAsync(new("wow", Build, "us", "1", new()), default);
        Assert.Equal(["root:7", "install:4", "install:5"], files.Entries.Select(e => e.Key));
        var root = files.Entries[0];
        Assert.Equal((3, 2, 1, "900", "1200", 1, "514"), (root.MembershipCount, root.ContentKeyCount, root.UnresolvedCount, root.EncodedBytesMin, root.EncodedBytesMax, root.StoredCount, root.LocaleFlags));
        Assert.Equal((2, "authored/same.bin", 1), (files.Entries[1].MembershipCount, files.Entries[1].Name, files.Entries[1].StoredCount));
        var filter = new InventoryFileFilter(LocaleFlags: "2");
        var first = await seeded.Catalog.ReadInventoryFileAsync(new("wow", Build, "us", "1", "root:7", filter, 2), default);
        Assert.Equal((3, 2, "2"), (first.MembershipCount, first.MatchingCount, first.NextCursor));
        Assert.Equal([true, false], first.Memberships.Select(m => m.Matches));
        var second = await seeded.Catalog.ReadInventoryFileAsync(new("wow", Build, "us", "1", "root:7", filter, 2, first.NextCursor), default);
        var last = Assert.Single(second.Memberships); Assert.Null(second.NextCursor);
        Assert.Equal(("3", true, Encrypted | NoNames), (last.Id, last.Matches, uint.Parse(last.ContentFlags!, CultureInfo.InvariantCulture)));
        var unresolved = first.Memberships[1]; Assert.Null(unresolved.EncodedBytes); Assert.Null(unresolved.EncodingKey); Assert.Null(unresolved.DecodedBytes);
        var same = await seeded.Catalog.ReadInventoryFileAsync(new("wow", Build, "us", "1", "install:4", new(StoredOnly: true)), default);
        Assert.Equal(["4", "6"], same.Memberships.Select(m => m.Id)); Assert.Equal([false, true], same.Memberships.Select(m => m.Matches));
        Assert.Equal((2, 1), (same.MembershipCount, same.MatchingCount));
    }

    [Theory]
    [InlineData("fdid=01")] [InlineData("fdid=4294967296")] [InlineData("ckey=ABCD")] [InlineData("ckey=abc")]
    [InlineData("ckey=" + SharedKey + "0")] [InlineData("fdid=1 ckey=abcd")] [InlineData("locale=0")] [InlineData("flags=0")]
    [InlineData("flags=any")] [InlineData("family=download")]
    public void Invalid_filters_are_refused_before_storage(string text) =>
        Assert.Equal("INVALID_REQUEST", Assert.Throws<DiscoveryException>(() => InventoryService.Parse(Filter(text))).Code);

    [Theory]
    [InlineData("root:-1")] [InlineData("root:4294967296")] [InlineData("install:01")] [InlineData("other:1")] [InlineData("root")] [InlineData("root:1:2")]
    public void Invalid_cursors_and_file_keys_are_refused(string key)
    {
        Assert.Throws<DiscoveryException>(() => InventoryService.Validate(new InventoryFilesRequest("wow", Build, "us", "1", new(), 50, key)));
        Assert.Throws<DiscoveryException>(() => InventoryService.Validate(new InventoryFileRequest("wow", Build, "us", "1", key, new())));
    }

    [Fact]
    public void Install_zero_is_a_cursor_but_not_a_file()
    {
        Assert.Equal(new InventoryFilePosition(true, 0), InventoryService.Validate(new InventoryFilesRequest("wow", Build, "us", "1", new(), 50, "install:0")).After);
        Assert.Throws<DiscoveryException>(() => InventoryService.Validate(new InventoryFileRequest("wow", Build, "us", "1", "install:0", new())));
        Assert.Throws<DiscoveryException>(() => InventoryService.Validate(new InventoryFilesRequest("wow", Build, "us", "1", new(), 101)));
    }

    [Fact]
    public async Task The_host_routes_flat_filters_and_refuses_unknown_or_mistyped_parameters()
    {
        await using var seeded = await Seeded.Create([new(1, "root", 42, null, SharedKey, 0x2, NoNames, true, 10), new(2, "root", 43, null, SharedKey, 0x2, NoNames, false, 10)]);
        var scope = new Dictionary<string, object> { ["productId"] = "wow", ["buildIdentity"] = Build, ["sourceRegion"] = "us", ["generation"] = "1" };
        object Params(params (string, object)[] extra) { var value = new Dictionary<string, object>(scope); foreach (var (k, v) in extra) value[k] = v; return value; }
        var replies = await Run(new InventoryService(seeded.Catalog),
            new { protocolVersion = 1, id = "hello", method = "hello", @params = new { } },
            new { protocolVersion = 1, id = "files", method = "inventory.files", @params = Params(("storedOnly", true), ("localeFlags", "2"), ("limit", 7)) },
            new { protocolVersion = 1, id = "file", method = "inventory.file", @params = Params(("file", "root:43"), ("storedOnly", true)) },
            new { protocolVersion = 1, id = "typed", method = "inventory.files", @params = Params(("storedOnly", "yes")) },
            new { protocolVersion = 1, id = "number", method = "inventory.files", @params = Params(("fileDataId", 42)) },
            new { protocolVersion = 1, id = "extra", method = "inventory.files", @params = Params(("platform", "Windows")) },
            new { protocolVersion = 1, id = "nofile", method = "inventory.file", @params = Params() });
        Assert.Contains(replies["hello"].GetProperty("result").GetProperty("capabilities").EnumerateArray(), c => c.GetString() == "inventory-files-v1");
        var files = replies["files"].GetProperty("result");
        Assert.Equal("root:42", Assert.Single(files.GetProperty("entries").EnumerateArray()).GetProperty("key").GetString());
        Assert.Equal(JsonValueKind.Null, files.GetProperty("totalCount").ValueKind); Assert.False(files.GetProperty("scanLimited").GetBoolean());
        var file = replies["file"].GetProperty("result");
        Assert.Equal(0, file.GetProperty("matchingCount").GetInt32()); Assert.False(file.GetProperty("memberships")[0].GetProperty("matches").GetBoolean());
        foreach (var id in new[] { "typed", "number", "extra", "nofile" }) Assert.Equal("INVALID_REQUEST", replies[id].GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_canceled_request_stops_inside_the_scan()
    {
        await using var seeded = await Seeded.Create(Rows());
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => seeded.Catalog.ReadInventoryFilesAsync(new("wow", Build, "us", "1", new(ContentFlags: "4")), canceled.Token));
    }

    // ---------- authored rows and the brute-force reference ----------
    internal sealed record Row(long Id, string Family, long? Fdid, string? Name, string CKey, uint Locale, uint Flags, bool Stored, long? Encoded, long? Group = null);
    private static string Key(Row row) => row.Family == "root" ? $"root:{row.Fdid}" : $"install:{row.Group}";
    private static bool Matches(InventoryFileQuery query, Row row) => query.Matches(row.Family, row.Fdid, row.CKey, row.Family == "root" ? row.Locale : null, row.Family == "root" ? row.Flags : null, row.Stored);
    private static List<string> Expected(IReadOnlyList<Row> rows, InventoryFileQuery query) =>
        rows.Where(r => Matches(query, r)).OrderBy(r => r.Family == "root" ? 0 : 1).ThenBy(r => r.Family == "root" ? r.Fdid : r.Group).Select(Key).Distinct().ToList();
    private static InventoryFileFilter Filter(string text)
    {
        var filter = new InventoryFileFilter();
        foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var (name, value) = part.Contains('=') ? (part[..part.IndexOf('=')], part[(part.IndexOf('=') + 1)..]) : (part, "");
            filter = name switch
            {
                "fdid" => filter with { FileDataId = value }, "ckey" => filter with { ContentKey = value }, "locale" => filter with { LocaleFlags = value },
                "flags" => filter with { ContentFlags = value }, "family" => filter with { Family = value }, "stored" => filter with { StoredOnly = true },
                _ => throw new ArgumentException(part),
            };
        }
        return filter;
    }
    // About 9,000 retail-shaped memberships: dense all-locale/NoNames variants, rare content bits, sparse stored marks,
    // a CKey shared by many FileDataIDs, and one variant pair whose duplicate FileDataIDs straddle a 64-row refill.
    private static List<Row> Rows()
    {
        var rows = new List<Row>(); var random = new Random(20); long id = 0;
        string Hex() { Span<byte> b = stackalloc byte[16]; random.NextBytes(b); var text = Convert.ToHexStringLower(b); return text.StartsWith("aaaa") || text.StartsWith("bbbb") || text.StartsWith("dddd") ? "e" + text[1..] : text; }
        for (var i = 0; i < 4000; i++)
        {
            var fdid = i * 3L + 1; var count = i % 5 == 0 ? 3 : i % 3 == 0 ? 2 : 1;
            for (var k = 0; k < count; k++)
            {
                var locale = k == 0 ? AllLocales : (i % 2 == 0 ? 0x2u : 0x200u);
                var flags = NoNames | (i % 97 == 5 ? 0x4u : 0) | (i % 11 == 0 && k > 0 ? Encrypted : 0);
                if (i % 401 == 7 && k == 0) flags = 0;
                var ckey = i % 13 == 0 && k == 0 ? SharedKey : i % 211 == 3 ? "bbbb" + i.ToString("x28") : Hex();
                rows.Add(new(++id, "root", fdid, null, ckey, locale, flags, (i * 7 + k) % 31 == 0, i % 509 == 4 && k == 1 ? null : 1000 + i));
            }
        }
        // The rare pair (locale 0x4000, flags 0x20): row 64 is the stored second copy of the FileDataID whose first copy is row 63.
        rows.Add(new(++id, "root", 50_000, null, Hex(), 0x4000, 0x20, false, 7));
        for (var i = 1; i <= 64; i++) { rows.Add(new(++id, "root", 50_000 + i, null, Hex(), 0x4000, 0x20, false, 7)); rows.Add(new(++id, "root", 50_000 + i, null, Hex(), 0x4000, 0x20, true, 7)); }
        for (var i = 0; i < 30; i++) rows.Add(new(++id, "install", null, $"authored/install-{i % 20:D2}.bin", i == 3 ? SharedKey : i == 4 ? "dddd0001" + i.ToString("x24") : Hex(), 0, 0, i % 4 == 0, 500 + i));
        rows.Add(new(++id, "root", 4001 * 3, null, Hex(), 0x8000000, NoNames, false, 1));
        return rows;
    }

    private sealed class Seeded : IAsyncDisposable
    {
        private readonly TempDirectory directory = new();
        public DiscoveryCatalog Catalog { get; private set; } = null!;
        public IReadOnlyList<Row> Rows { get; private set; } = [];
        public static async Task<Seeded> Create(IReadOnlyList<Row> rows)
        {
            var seeded = new Seeded(); seeded.Catalog = await DiscoveryCatalog.OpenAsync(seeded.directory.Path);
            var groups = new Dictionary<string, long>(StringComparer.Ordinal);
            using (var db = new SqliteConnection($"Data Source={Path.Combine(seeded.directory.Path, "discovery-v1.sqlite3")};Pooling=False"))
            {
                db.Open(); using var tx = db.BeginTransaction();
                using (var setup = db.CreateCommand())
                {
                    setup.Transaction = tx;
                    setup.CommandText = $"""
                        INSERT INTO builds VALUES('{Build}','wow','authored','{"{}"}','2026-10-18T12:00:00Z','2026-10-18T12:00:00Z',1,1,0);
                        INSERT INTO inventory_generations(id,build_id,region,state,profile,summary,metadata,source) VALUES(1,'{Build}','us','ready','home-inventory-v1','{"{}"}','[]','{"{}"}');
                        """;
                    setup.ExecuteNonQuery();
                }
                using var insert = db.CreateCommand(); insert.Transaction = tx;
                insert.CommandText = "INSERT INTO inventory_origins(g,id,family,name,fdid,ckey,width,locale,flags,name_hash,raw,ekey,encoded,decoded,fdid_n,locale_n,flags_n,grp,stored) VALUES(1,$i,$f,$n,$d,$c,0,$l,$a,NULL,$raw,$e,$enc,$dec,$dn,$ln,$an,$grp,$s)";
                foreach (var name in new[] { "$i", "$f", "$n", "$d", "$c", "$l", "$a", "$raw", "$e", "$enc", "$dec", "$dn", "$ln", "$an", "$grp", "$s" }) insert.Parameters.Add(new SqliteParameter(name, DBNull.Value));
                foreach (var r in rows)
                {
                    var root = r.Family == "root";
                    long? group = root ? null : groups.TryGetValue(r.Name!, out var first) ? first : groups[r.Name!] = r.Id;
                    object? V(object? value) => value ?? DBNull.Value;
                    var values = new object?[] { r.Id, r.Family, r.Name, root ? r.Fdid!.Value.ToString(CultureInfo.InvariantCulture) : null, r.CKey,
                        root ? r.Locale.ToString(CultureInfo.InvariantCulture) : null, root ? r.Flags.ToString(CultureInfo.InvariantCulture) : null,
                        root ? JsonSerializer.Serialize(new { blockOrdinal = 0, entryOrdinal = r.Id, RawContentFlags = new { First = r.Flags, Second = 0u, Third = 0 } }) : r.Id.ToString(CultureInfo.InvariantCulture),
                        r.Encoded is null ? null : r.CKey, r.Encoded, r.Encoded is null ? null : r.Encoded * 2, root ? r.Fdid : null, root ? (long)r.Locale : null, root ? (long)r.Flags : null, group, r.Stored ? 1 : 0 };
                    for (var i = 0; i < values.Length; i++) insert.Parameters[i].Value = V(values[i]);
                    insert.ExecuteNonQuery();
                }
                tx.Commit();
            }
            seeded.Rows = rows.Select(r => r.Family == "install" ? r with { Group = groups[r.Name!] } : r).ToList();
            try { await seeded.Catalog.RecordFileStatsAsync(1, default); }
            catch { await seeded.DisposeAsync(); throw; }
            return seeded;
        }
        public async ValueTask DisposeAsync() { await Catalog.DisposeAsync(); directory.Dispose(); }
    }

    private static string InstallId(InventoryFilesPage page) => page.Entries.Single(e => e.Family == "install").Key["install:".Length..];

    // Input stays open until every reply arrived: the host cancels requests still running when its input ends.
    private static async Task<Dictionary<string, JsonElement>> Run(InventoryService inventory, params object[] requests)
    {
        var host = new ProtocolHost(new DiscoveryService(new Unused(), new Unused()), inventory: inventory);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var input = new System.IO.Pipelines.Pipe(); var output = new Lines();
        var running = host.RunAsync(input.Reader.AsStream(), output);
        await input.Writer.WriteAsync(Encoding.UTF8.GetBytes(string.Join('\n', requests.Select(request => JsonSerializer.Serialize(request, json))) + "\n"));
        await output.WaitForAsync(requests.Length).WaitAsync(TimeSpan.FromSeconds(10));
        await input.Writer.CompleteAsync(); await running.WaitAsync(TimeSpan.FromSeconds(10));
        return output.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); })
            .ToDictionary(reply => reply.GetProperty("id").GetString()!);
    }
    private sealed class Lines : Stream
    {
        private readonly StringBuilder text = new(); private readonly SemaphoreSlim line = new(0);
        public string Text { get { lock (text) return text.ToString(); } }
        public async Task WaitForAsync(int count) { for (var i = 0; i < count; i++) await line.WaitAsync(); }
        public override void Write(byte[] buffer, int offset, int count)
        {
            var value = Encoding.UTF8.GetString(buffer, offset, count);
            lock (text) text.Append(value);
            foreach (var c in value) if (c == '\n') line.Release();
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) { Write(buffer, offset, count); return Task.CompletedTask; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) { Write(buffer.ToArray(), 0, buffer.Length); return ValueTask.CompletedTask; }
        public override void Flush() { }
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
    private sealed class Unused : IDiscoverySource, IDiscoveryCatalog
    {
        public Task<DiscoveryAttempt> FetchAsync(string product, CancellationToken token) => throw new InvalidOperationException();
        public Task<long> BeginRefreshAsync(string product, CancellationToken token) => throw new InvalidOperationException();
        public Task<DiscoveryPage> CommitAsync(string product, long generation, DiscoveryAttempt attempt, string completedAt, CancellationToken token) => throw new InvalidOperationException();
        public Task<DiscoveryPage> ReadAsync(string product, string filter, int limit, string? cursor, CancellationToken token) => throw new InvalidOperationException();
        public Task<DiscoveryCatalogPage> ReadAllAsync(string? product, string filter, int limit, string? cursor, CancellationToken token) => throw new InvalidOperationException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // The real metadata-only preparation over the authored InventoryFixture.
    private sealed class Prepared : IAsyncDisposable
    {
        public InventoryFixture Fixture { get; } = InventoryFixture.Create();
        private readonly TempDirectory directory = new();
        public DiscoveryCatalog Catalog { get; private set; } = null!;
        private VerifiedObjectStore objects = null!; private HttpClient client = null!; private InventoryWorkspacePreparer preparer = null!;
        public Handler Handler { get; private set; } = null!;
        public static async Task<Prepared> Create()
        {
            var h = new Prepared(); h.Catalog = await DiscoveryCatalog.OpenAsync(h.directory.Path); h.objects = new(h.directory.Path);
            h.Handler = new(h.Fixture); h.client = new(h.Handler); h.preparer = new(h.Catalog, h.objects, h.client);
            var g = await h.Catalog.BeginRefreshAsync("wow", default); await h.Catalog.CommitAsync("wow", g, h.Fixture.Discovery, DateTimeOffset.UtcNow.ToString("O"), default);
            return h;
        }
        public Task<WorkspaceSnapshot> Open() => preparer.PrepareAsync(new("wow", Fixture.BuildIdentity, "us"), _ => Task.CompletedTask, default);
        public async Task Execute(string sql)
        {
            await using var db = new SqliteConnection($"Data Source={Path.Combine(directory.Path, "discovery-v1.sqlite3")};Pooling=False"); await db.OpenAsync();
            await using var cmd = db.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync();
        }
        public async Task<long> Scalar(string sql)
        {
            await using var db = new SqliteConnection($"Data Source={Path.Combine(directory.Path, "discovery-v1.sqlite3")};Pooling=False"); await db.OpenAsync();
            await using var cmd = db.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }
        /// <summary>Closes the engine's catalog, changes it on disk, and opens it again (running any upgrade steps).</summary>
        public async Task Restart(string sql)
        {
            preparer.Dispose(); await objects.DisposeAsync(); await Catalog.DisposeAsync();
            await Execute(sql);
            Catalog = await DiscoveryCatalog.OpenAsync(directory.Path); objects = new(directory.Path); preparer = new(Catalog, objects, client);
        }
        public async ValueTask DisposeAsync() { preparer.Dispose(); client.Dispose(); await objects.DisposeAsync(); await Catalog.DisposeAsync(); directory.Dispose(); }
    }
    internal sealed class Handler(InventoryFixture fixture) : HttpMessageHandler
    {
        public bool BlockAll;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (BlockAll) throw new InvalidOperationException("No HTTP is allowed during local reuse.");
            if (!fixture.Responses.TryGetValue(request.RequestUri!.AbsoluteUri, out var bytes)) throw new InvalidOperationException("Unexpected request.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes.ToArray()) });
        }
    }
}
