using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Host;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class SettingsTests
{
    private static readonly DownloadLimitSettings Limits = new(4, 2, null, false);
    private static SettingsService Service(string directory, TimeSpan? budget = null, IStorageMeasurer? measurer = null) =>
        new(new JsonSettingsStore(directory), measurer ?? new DataDirectoryMeasurer(directory), Limits, measurementBudget: budget);
    private static string SettingsFile(TempDirectory directory) => Path.Combine(directory.Path, JsonSettingsStore.FileName);

    [Fact]
    public async Task A_missing_file_reads_as_defaults_that_were_never_saved()
    {
        using var directory = new TempDirectory(); Directory.CreateDirectory(directory.Path);
        var snapshot = await Service(directory.Path).ReadAsync();
        Assert.Equal(SettingsFileState.Absent, snapshot.File);
        Assert.Null(snapshot.Problem); Assert.Null(snapshot.LastChangedAt);
        // Owner decision on #9: the community build list is on by default (D106).
        Assert.Equal(new BooleanSetting(true, true, SettingSource.Default, null), snapshot.CommunityBuildHistory);
        Assert.True(await Service(directory.Path).CommunityBuildHistoryEnabledAsync(default));
        Assert.Equal(Path.GetFullPath(directory.Path), snapshot.DataDirectory);
        Assert.Equal(Limits, snapshot.DownloadLimits);
        Assert.False(File.Exists(SettingsFile(directory)), "Reading never writes a settings file.");
    }

    [Fact]
    public async Task A_saved_value_survives_a_new_engine_and_leaves_no_temporary_file()
    {
        using var directory = new TempDirectory(); Directory.CreateDirectory(directory.Path);
        var committed = await Service(directory.Path).SetAsync(SettingsService.CommunityBuildHistory, true);
        Assert.Equal(new BooleanSetting(true, true, SettingSource.Saved, null), committed.CommunityBuildHistory);
        Assert.Equal(SettingsFileState.Read, committed.File);
        Assert.NotNull(committed.LastChangedAt);
        Assert.Equal(TimeSpan.Zero, committed.LastChangedAt!.Value.Offset);
        Assert.False(File.Exists(Path.Combine(directory.Path, JsonSettingsStore.TemporaryFileName)));
        var restarted = await Service(directory.Path).ReadAsync();
        Assert.Equal(committed, restarted);
        using var saved = JsonDocument.Parse(await File.ReadAllBytesAsync(SettingsFile(directory)));
        Assert.Equal(1, saved.RootElement.GetProperty("version").GetInt32());
        Assert.True(saved.RootElement.GetProperty("values").GetProperty("communityBuildHistory").GetBoolean());
        var off = await Service(directory.Path).SetAsync(SettingsService.CommunityBuildHistory, false);
        Assert.Equal(new BooleanSetting(false, true, SettingSource.Saved, null), off.CommunityBuildHistory);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("[]")]
    [InlineData("{\"values\":{}}")]
    [InlineData("{\"version\":\"1\",\"values\":{}}")]
    [InlineData("{\"version\":0,\"values\":{}}")]
    [InlineData("{\"version\":1,\"values\":[]}")]
    [InlineData("{\"version\":1,\"version\":1,\"values\":{}}")]
    [InlineData("{\"version\":1,\"values\":{\"communityBuildHistory\":true,\"communityBuildHistory\":false}}")]
    public async Task An_unreadable_file_uses_the_default_says_so_and_a_switch_replaces_it(string content)
    {
        using var directory = new TempDirectory(); Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(SettingsFile(directory), content);
        var service = Service(directory.Path);
        var snapshot = await service.ReadAsync();
        Assert.Equal(SettingsFileState.Unreadable, snapshot.File);
        Assert.Equal("SETTINGS_UNREADABLE", snapshot.Problem?.Code);
        // Fails closed like D081: an unreadable file reads as off, so nothing is fetched from wago.tools.
        Assert.False(snapshot.CommunityBuildHistory.Value);
        Assert.Equal(SettingSource.Unreadable, snapshot.CommunityBuildHistory.Source);
        Assert.False(await service.CommunityBuildHistoryEnabledAsync(default));
        var committed = await service.SetAsync(SettingsService.CommunityBuildHistory, true);
        Assert.Equal(SettingsFileState.Read, committed.File);
        Assert.Equal(new BooleanSetting(true, true, SettingSource.Saved, null), committed.CommunityBuildHistory);
    }

    [Fact]
    public async Task Invalid_UTF8_and_oversized_files_are_unreadable_without_being_parsed()
    {
        using var directory = new TempDirectory(); Directory.CreateDirectory(directory.Path);
        await File.WriteAllBytesAsync(SettingsFile(directory), [0x7b, 0xff, 0xfe, 0x7d]);
        Assert.Equal("SETTINGS_UNREADABLE", (await Service(directory.Path).ReadAsync()).Problem?.Code);
        await File.WriteAllTextAsync(SettingsFile(directory), "{\"version\":1,\"pad\":\"" + new string('x', JsonSettingsStore.MaxFileBytes) + "\"}");
        var oversized = await Service(directory.Path).ReadAsync();
        Assert.Equal(SettingsFileState.Unreadable, oversized.File);
        Assert.Contains("larger", oversized.Problem!.Message);
    }

    [Fact]
    public async Task A_file_whose_bytes_were_never_parsed_is_never_replaced()
    {
        using var directory = new TempDirectory(); Directory.CreateDirectory(directory.Path);
        var oversized = "{\"version\":1,\"pad\":\"" + new string('x', JsonSettingsStore.MaxFileBytes) + "\"}";
        await File.WriteAllTextAsync(SettingsFile(directory), oversized);
        var service = Service(directory.Path);
        var large = await service.ReadAsync();
        Assert.Equal(SettingsFileState.Unreadable, large.File);
        Assert.Equal(new SettingsProblem("SETTINGS_NOT_READ", large.Problem!.Message, false), large.Problem);
        var refused = await Assert.ThrowsAsync<DiscoveryException>(() => service.SetAsync(SettingsService.CommunityBuildHistory, true));
        Assert.Equal("SETTINGS_NOT_READ", refused.Code);
        Assert.False(refused.Retryable);
        Assert.Equal(oversized, await File.ReadAllTextAsync(SettingsFile(directory)));
        // A file that can't be opened (here a folder in its place, as a lock or permission error would) is left alone and retryable.
        File.Delete(SettingsFile(directory)); Directory.CreateDirectory(SettingsFile(directory));
        var locked = await service.ReadAsync();
        Assert.Equal(new SettingsProblem("SETTINGS_NOT_READ", locked.Problem!.Message, true), locked.Problem);
        Assert.Equal(SettingSource.Unreadable, locked.CommunityBuildHistory.Source);
        var busy = await Assert.ThrowsAsync<DiscoveryException>(() => service.SetAsync(SettingsService.CommunityBuildHistory, true));
        Assert.Equal("SETTINGS_NOT_READ", busy.Code);
        Assert.True(busy.Retryable);
        Assert.True(Directory.Exists(SettingsFile(directory)));
        Assert.False(File.Exists(Path.Combine(directory.Path, JsonSettingsStore.TemporaryFileName)));
    }

    [Theory]
    [InlineData("{\"version\":2,\"values\":{\"later\":[[[[[[[1]]]]]]]}}", "2")]
    [InlineData("{\"version\":3000000000,\"values\":{\"communityBuildHistory\":true}}", "3000000000")]
    [InlineData("{\"version\":123456789012345678901234567890}", "123456789012345678901234567890")]
    public async Task A_newer_version_is_recognised_whatever_its_depth_or_size_and_is_never_replaced(string content, string version)
    {
        using var directory = new TempDirectory(); Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(SettingsFile(directory), content);
        var service = Service(directory.Path);
        var snapshot = await service.ReadAsync();
        Assert.Equal(SettingsFileState.Newer, snapshot.File);
        Assert.Equal($"The settings file is version {version}, saved by a newer VibeMiner.", snapshot.Problem?.Message);
        Assert.Equal("SETTINGS_VERSION", (await Assert.ThrowsAsync<DiscoveryException>(() => service.SetAsync(SettingsService.CommunityBuildHistory, false))).Code);
        Assert.Equal(content, await File.ReadAllTextAsync(SettingsFile(directory)));
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("-1")]
    [InlineData("1e3")]
    [InlineData("01")]
    public async Task A_version_that_is_not_a_positive_integer_is_unreadable(string version)
    {
        using var directory = new TempDirectory(); Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(SettingsFile(directory), "{\"version\":" + version + ",\"values\":{}}");
        Assert.Equal(SettingsFileState.Unreadable, (await Service(directory.Path).ReadAsync()).File);
    }

    [Fact]
    public async Task Many_and_deeply_nested_unknown_values_are_all_kept_on_a_write()
    {
        using var directory = new TempDirectory(); Directory.CreateDirectory(directory.Path);
        var deep = new string('[', 100) + "1" + new string(']', 100);
        var values = string.Join(",", Enumerable.Range(0, 70).Select(index => $"\"later{index}\":{index}"));
        await File.WriteAllTextAsync(SettingsFile(directory), "{\"version\":1,\"values\":{" + values + ",\"deep\":" + deep + "}}");
        var service = Service(directory.Path);
        Assert.Equal(SettingsFileState.Read, (await service.ReadAsync()).File);
        await service.SetAsync(SettingsService.CommunityBuildHistory, true);
        using var saved = JsonDocument.Parse(await File.ReadAllBytesAsync(SettingsFile(directory)), new JsonDocumentOptions { MaxDepth = 200 });
        var kept = saved.RootElement.GetProperty("values");
        Assert.Equal(72, kept.EnumerateObject().Count());
        Assert.Equal(69, kept.GetProperty("later69").GetInt32());
        Assert.Equal(deep, kept.GetProperty("deep").GetRawText());
        Assert.True(kept.GetProperty("communityBuildHistory").GetBoolean());
    }

    [Theory]
    [InlineData("name-escape", "{\"version\":1,\"values\":{\"\\ud800\":true}}")]
    [InlineData("value-escape", "{\"version\":1,\"values\":{\"later\":\"\\ud800\"}}")]
    [InlineData("setting-value-escape", "{\"version\":1,\"values\":{\"communityBuildHistory\":\"\\ud800\"}}")]
    [InlineData("changed-at-escape", "{\"version\":1,\"changedAt\":\"\\ud800\",\"values\":{}}")]
    [InlineData("top-level-name", "{\"version\":1,\"\\udc00\":1,\"values\":{}}")]
    public async Task Undecodable_text_in_a_name_or_value_is_a_damaged_file_not_an_engine_failure(string kind, string content)
    {
        using var directory = new TempDirectory(); Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(SettingsFile(directory), content);
        var service = Service(directory.Path);
        var snapshot = await service.ReadAsync();
        Assert.True(snapshot.File == SettingsFileState.Unreadable, kind);
        Assert.Equal("SETTINGS_UNREADABLE", snapshot.Problem?.Code);
        Assert.True((await service.SetAsync(SettingsService.CommunityBuildHistory, true)).CommunityBuildHistory.Value);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("value")]
    public async Task Invalid_UTF8_bytes_inside_values_are_a_damaged_file(string where)
    {
        using var directory = new TempDirectory(); Directory.CreateDirectory(directory.Path);
        var bad = new byte[] { 0xC3, 0x28 };
        var bytes = where == "name"
            ? [.. Fixtures.Utf8("{\"version\":1,\"values\":{\""), .. bad, .. Fixtures.Utf8("\":true}}")]
            : (byte[])[.. Fixtures.Utf8("{\"version\":1,\"values\":{\"later\":\""), .. bad, .. Fixtures.Utf8("\"}}")];
        await File.WriteAllBytesAsync(SettingsFile(directory), bytes);
        var snapshot = await Service(directory.Path).ReadAsync();
        Assert.Equal(SettingsFileState.Unreadable, snapshot.File);
        Assert.Equal("SETTINGS_UNREADABLE", snapshot.Problem?.Code);
    }

    [Theory]
    [InlineData("value-escape")]
    [InlineData("name-escape")]
    [InlineData("value-utf8")]
    public async Task Undecodable_text_in_a_newer_file_still_reads_as_newer_and_is_never_replaced(string kind)
    {
        using var directory = new TempDirectory(); Directory.CreateDirectory(directory.Path);
        byte[] content = kind switch
        {
            "value-escape" => Fixtures.Utf8("{\"version\":2,\"future\":\"\\ud800\"}"),
            "name-escape" => Fixtures.Utf8("{\"version\":2,\"\\ud800\":1,\"values\":{}}"),
            _ => [.. Fixtures.Utf8("{\"version\":2,\"values\":{\"later\":\""), 0xC3, 0x28, .. Fixtures.Utf8("\"}}")],
        };
        await File.WriteAllBytesAsync(SettingsFile(directory), content);
        var service = Service(directory.Path);
        var snapshot = await service.ReadAsync();
        Assert.True(snapshot.File == SettingsFileState.Newer, kind);
        Assert.Equal("SETTINGS_VERSION", snapshot.Problem?.Code);
        Assert.Equal("SETTINGS_VERSION", (await Assert.ThrowsAsync<DiscoveryException>(() => service.SetAsync(SettingsService.CommunityBuildHistory, true))).Code);
        Assert.Equal(content, await File.ReadAllBytesAsync(SettingsFile(directory)));
    }

    [Fact]
    public async Task Unknown_top_level_fields_are_kept_as_written()
    {
        using var directory = new TempDirectory(); Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(SettingsFile(directory), "{\"version\":1,\"later\":{\"limit\":3,\"list\":[1,2]},\"note\":\"kept\",\"values\":{\"communityBuildHistory\":false}}");
        await Service(directory.Path).SetAsync(SettingsService.CommunityBuildHistory, true);
        using var saved = JsonDocument.Parse(await File.ReadAllBytesAsync(SettingsFile(directory)));
        var root = saved.RootElement;
        Assert.Equal("{\"limit\":3,\"list\":[1,2]}", root.GetProperty("later").GetRawText());
        Assert.Equal("kept", root.GetProperty("note").GetString());
        Assert.True(root.GetProperty("values").GetProperty("communityBuildHistory").GetBoolean());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal(["version", "changedAt", "values", "later", "note"], root.EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public async Task A_byte_order_mark_is_not_content()
    {
        using var directory = new TempDirectory(); Directory.CreateDirectory(directory.Path);
        byte[] bom = [0xEF, 0xBB, 0xBF];
        await File.WriteAllBytesAsync(SettingsFile(directory), [.. bom, .. Fixtures.Utf8("{\"version\":1,\"values\":{\"communityBuildHistory\":true}}")]);
        var read = await Service(directory.Path).ReadAsync();
        Assert.Equal(new BooleanSetting(true, true, SettingSource.Saved, null), read.CommunityBuildHistory);
        byte[] newer = [.. bom, .. Fixtures.Utf8("{\"version\":2,\"values\":{}}")];
        await File.WriteAllBytesAsync(SettingsFile(directory), newer);
        var service = Service(directory.Path);
        Assert.Equal(SettingsFileState.Newer, (await service.ReadAsync()).File);
        Assert.Equal("SETTINGS_VERSION", (await Assert.ThrowsAsync<DiscoveryException>(() => service.SetAsync(SettingsService.CommunityBuildHistory, true))).Code);
        Assert.Equal(newer, await File.ReadAllBytesAsync(SettingsFile(directory)));
    }

    [Fact]
    public async Task One_bad_value_is_unreadable_on_its_own_and_other_values_are_kept()
    {
        using var directory = new TempDirectory(); Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(SettingsFile(directory),
            "{\"version\":1,\"changedAt\":\"2026-10-18T12:02:00.0000000+00:00\",\"values\":{\"communityBuildHistory\":\"yes\",\"laterSetting\":{\"limit\":3}}}");
        var service = Service(directory.Path);
        var snapshot = await service.ReadAsync();
        Assert.Equal(SettingsFileState.Read, snapshot.File);
        Assert.Null(snapshot.Problem);
        Assert.Equal(new DateTimeOffset(2026, 10, 18, 12, 2, 0, TimeSpan.Zero), snapshot.LastChangedAt);
        Assert.Equal(SettingSource.Unreadable, snapshot.CommunityBuildHistory.Source);
        Assert.Equal("SETTING_UNREADABLE", snapshot.CommunityBuildHistory.Problem?.Code);
        Assert.False(snapshot.CommunityBuildHistory.Value);
        await service.SetAsync(SettingsService.CommunityBuildHistory, false);
        using var saved = JsonDocument.Parse(await File.ReadAllBytesAsync(SettingsFile(directory)));
        var values = saved.RootElement.GetProperty("values");
        Assert.False(values.GetProperty("communityBuildHistory").GetBoolean());
        Assert.Equal(3, values.GetProperty("laterSetting").GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task Fetching_table_definitions_is_on_by_default_and_saves_like_any_switch()
    {
        using var directory = new TempDirectory(); Directory.CreateDirectory(directory.Path);
        var service = Service(directory.Path);
        Assert.Equal(new BooleanSetting(true, true, SettingSource.Default, null), (await service.ReadAsync()).FetchTableDefinitions);
        Assert.True(await service.FetchEnabledAsync(default));
        var off = await service.SetAsync(SettingsService.FetchTableDefinitions, false);
        Assert.Equal(new BooleanSetting(false, true, SettingSource.Saved, null), off.FetchTableDefinitions);
        // The other switch keeps having no saved value: a change writes only what it changes.
        Assert.Equal(SettingSource.Default, off.CommunityBuildHistory.Source);
        Assert.False(await Service(directory.Path).FetchEnabledAsync(default));
        using var saved = JsonDocument.Parse(await File.ReadAllBytesAsync(SettingsFile(directory)));
        var values = saved.RootElement.GetProperty("values");
        Assert.Equal(1, saved.RootElement.GetProperty("version").GetInt32());
        Assert.False(values.GetProperty("fetchTableDefinitions").GetBoolean());
        Assert.False(values.TryGetProperty("communityBuildHistory", out _));
    }

    [Fact]
    public async Task A_file_from_before_the_switch_reads_it_as_on_and_a_change_keeps_every_other_value()
    {
        using var directory = new TempDirectory(); Directory.CreateDirectory(directory.Path);
        // A settings-v1 file the #8 engine wrote, plus a bad value of the other switch and an unknown one.
        await File.WriteAllTextAsync(SettingsFile(directory),
            "{\"version\":1,\"changedAt\":\"2026-10-18T12:02:00.0000000+00:00\",\"values\":{\"communityBuildHistory\":\"yes\",\"laterSetting\":[1,2]},\"later\":true}");
        var service = Service(directory.Path);
        var before = await service.ReadAsync();
        Assert.Equal(new BooleanSetting(true, true, SettingSource.Default, null), before.FetchTableDefinitions);
        Assert.True(await service.FetchEnabledAsync(default));
        Assert.Equal(SettingSource.Unreadable, before.CommunityBuildHistory.Source);
        var committed = await service.SetAsync(SettingsService.FetchTableDefinitions, false);
        Assert.Equal(SettingSource.Unreadable, committed.CommunityBuildHistory.Source);
        using var saved = JsonDocument.Parse(await File.ReadAllBytesAsync(SettingsFile(directory)));
        var values = saved.RootElement.GetProperty("values");
        Assert.Equal("yes", values.GetProperty("communityBuildHistory").GetString());
        Assert.Equal("[1,2]", values.GetProperty("laterSetting").GetRawText());
        Assert.False(values.GetProperty("fetchTableDefinitions").GetBoolean());
        Assert.True(saved.RootElement.GetProperty("later").GetBoolean());
        // Switching the other one replaces only its own bad value and keeps this one.
        var community = await service.SetAsync(SettingsService.CommunityBuildHistory, true);
        Assert.True(community.CommunityBuildHistory.Value); Assert.False(community.FetchTableDefinitions.Value);
    }

    [Fact]
    public async Task Switching_one_setting_in_a_damaged_file_saves_the_others_as_shown()
    {
        using var directory = new TempDirectory(); Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(SettingsFile(directory), "{broken");
        var service = Service(directory.Path);
        Assert.False((await service.ReadAsync()).FetchTableDefinitions.Value);
        var committed = await service.SetAsync(SettingsService.CommunityBuildHistory, true);
        // Fetching was shown Off while the file was damaged; turning Community on never turns fetching back on.
        Assert.Equal(new BooleanSetting(false, true, SettingSource.Saved, null), committed.FetchTableDefinitions);
        Assert.False(await service.FetchEnabledAsync(default));
        using var saved = JsonDocument.Parse(await File.ReadAllBytesAsync(SettingsFile(directory)));
        Assert.False(saved.RootElement.GetProperty("values").GetProperty("fetchTableDefinitions").GetBoolean());
        // A missing file keeps the other switch at its default, unsaved.
        File.Delete(SettingsFile(directory));
        var fresh = await service.SetAsync(SettingsService.CommunityBuildHistory, true);
        Assert.Equal(SettingSource.Default, fresh.FetchTableDefinitions.Source);
    }

    [Theory]
    [InlineData("{\"version\":1,\"values\":{\"fetchTableDefinitions\":\"off\"}}", "SETTING_UNREADABLE")]
    [InlineData("{\"version\":2,\"values\":{\"fetchTableDefinitions\":false}}", "SETTINGS_VERSION")]
    [InlineData("{broken", "SETTINGS_UNREADABLE")]
    public async Task An_unreadable_fetch_value_or_newer_file_reads_as_off_and_fetches_nothing(string content, string code)
    {
        using var directory = new TempDirectory(); Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(SettingsFile(directory), content);
        var service = Service(directory.Path);
        var snapshot = await service.ReadAsync();
        Assert.Equal(SettingSource.Unreadable, snapshot.FetchTableDefinitions.Source);
        Assert.False(snapshot.FetchTableDefinitions.Value); Assert.True(snapshot.FetchTableDefinitions.DefaultValue);
        Assert.Equal(code, snapshot.FetchTableDefinitions.Problem!.Code);
        Assert.False(await service.FetchEnabledAsync(default));
        if (code == "SETTINGS_VERSION")
        {
            Assert.Equal("SETTINGS_VERSION", (await Assert.ThrowsAsync<DiscoveryException>(() => service.SetAsync(SettingsService.FetchTableDefinitions, true))).Code);
            Assert.Equal(content, await File.ReadAllTextAsync(SettingsFile(directory)));
        }
        else Assert.False((await service.SetAsync(SettingsService.FetchTableDefinitions, false)).FetchTableDefinitions.Value);
    }

    [Fact]
    public async Task A_newer_settings_version_is_reported_and_never_overwritten()
    {
        using var directory = new TempDirectory(); Directory.CreateDirectory(directory.Path);
        const string newer = "{\"version\":2,\"values\":{\"communityBuildHistory\":true}}";
        await File.WriteAllTextAsync(SettingsFile(directory), newer);
        var service = Service(directory.Path);
        var snapshot = await service.ReadAsync();
        Assert.Equal(SettingsFileState.Newer, snapshot.File);
        Assert.Equal("SETTINGS_VERSION", snapshot.Problem?.Code);
        Assert.Equal(new BooleanSetting(false, true, SettingSource.Unreadable, snapshot.Problem), snapshot.CommunityBuildHistory);
        var refused = await Assert.ThrowsAsync<DiscoveryException>(() => service.SetAsync(SettingsService.CommunityBuildHistory, false));
        Assert.Equal("SETTINGS_VERSION", refused.Code);
        Assert.Equal(newer, await File.ReadAllTextAsync(SettingsFile(directory)));
    }

    [Fact]
    public async Task A_failed_write_keeps_the_saved_file_and_reports_a_retryable_typed_failure()
    {
        using var directory = new TempDirectory(); Directory.CreateDirectory(directory.Path);
        var service = Service(directory.Path);
        await service.SetAsync(SettingsService.CommunityBuildHistory, false);
        var before = await File.ReadAllBytesAsync(SettingsFile(directory));
        // A folder where the temporary file goes makes the write fail on every platform.
        Directory.CreateDirectory(Path.Combine(directory.Path, JsonSettingsStore.TemporaryFileName));
        var failure = await Assert.ThrowsAsync<DiscoveryException>(() => service.SetAsync(SettingsService.CommunityBuildHistory, true));
        Assert.Equal("SETTINGS_WRITE_FAILED", failure.Code);
        Assert.True(failure.Retryable);
        Assert.Equal(before, await File.ReadAllBytesAsync(SettingsFile(directory)));
        Assert.Equal(new BooleanSetting(false, true, SettingSource.Saved, null), (await service.ReadAsync()).CommunityBuildHistory);
        Directory.Delete(Path.Combine(directory.Path, JsonSettingsStore.TemporaryFileName));
        Assert.True((await service.SetAsync(SettingsService.CommunityBuildHistory, true)).CommunityBuildHistory.Value);
    }

    [Fact]
    public async Task A_canceled_change_before_commit_writes_nothing_and_unknown_settings_are_refused()
    {
        using var directory = new TempDirectory(); Directory.CreateDirectory(directory.Path);
        var service = Service(directory.Path);
        using var canceled = new CancellationTokenSource(); await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SetAsync(SettingsService.CommunityBuildHistory, true, canceled.Token));
        Assert.False(File.Exists(SettingsFile(directory)));
        Assert.Equal("INVALID_REQUEST", (await Assert.ThrowsAsync<DiscoveryException>(() => service.SetAsync("bandwidthCap", true))).Code);
    }

    [Fact]
    public async Task Measurement_sums_exact_file_lengths_without_following_links()
    {
        using var directory = new TempDirectory(); using var outside = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, "objects-v1", "aa", "bb"));
        Directory.CreateDirectory(outside.Path);
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "discovery-v1.sqlite3"), new byte[1000]);
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "objects-v1", "aa", "bb", "object"), new byte[2345]);
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "objects-v1", "aa", "small"), new byte[7]);
        await File.WriteAllBytesAsync(Path.Combine(outside.Path, "elsewhere"), new byte[50_000]);
        var linked = true;
        try { Directory.CreateSymbolicLink(Path.Combine(directory.Path, "link"), outside.Path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { linked = false; }
        var before = DateTimeOffset.UtcNow;
        var measured = await new DataDirectoryMeasurer(directory.Path).MeasureAsync(CancellationToken.None);
        Assert.Equal(3352, measured.Bytes);
        Assert.Equal(3, measured.Files);
        Assert.Equal(0, measured.Skipped);
        Assert.True(measured.StartedAt >= before.AddSeconds(-1) && measured.MeasuredAt >= measured.StartedAt);
        if (linked) Assert.True(Directory.Exists(Path.Combine(directory.Path, "link")));
    }

    [Fact]
    public async Task Measurement_counts_folders_past_its_depth_bound_as_skipped_and_stops_when_canceled()
    {
        using var directory = new TempDirectory();
        var deep = directory.Path;
        for (var level = 0; level <= DataDirectoryMeasurer.MaxDepth; level++) deep = Path.Combine(deep, "d");
        Directory.CreateDirectory(deep);
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "top"), new byte[11]);
        await File.WriteAllBytesAsync(Path.Combine(deep, "too-deep"), new byte[13]);
        var measured = await new DataDirectoryMeasurer(directory.Path).MeasureAsync(CancellationToken.None);
        Assert.Equal(11, measured.Bytes);
        Assert.Equal(1, measured.Skipped);
        using var canceled = new CancellationTokenSource(); await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DataDirectoryMeasurer(directory.Path).MeasureAsync(canceled.Token));
    }

    [Fact]
    public async Task Measurement_leaves_its_caller_before_the_first_synchronous_batch()
    {
        using var directory = new TempDirectory(); Directory.CreateDirectory(directory.Path);
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "small"), new byte[5]);
        // The host runs each request inline until its first await, so a walk that starts synchronously stalls the dispatch loop.
        var walk = new DataDirectoryMeasurer(directory.Path).MeasureAsync(CancellationToken.None);
        Assert.False(walk.IsCompleted);
        Assert.Equal(5, (await walk).Bytes);
    }

    [Fact]
    public async Task The_host_answers_other_requests_and_cancellation_while_a_walk_runs()
    {
        using var directory = new TempDirectory();
        for (var folder = 0; folder < 40; folder++)
        {
            var path = Path.Combine(directory.Path, folder.ToString("x2"));
            Directory.CreateDirectory(path);
            for (var file = 0; file < 100; file++) await File.WriteAllBytesAsync(Path.Combine(path, file.ToString()), [1]);
        }
        var replies = await Run(Service(directory.Path), Request("measure", "settings.storage.measure", new { }),
            Request("hello", "hello", new { }), Request("cancel", "request.cancel", new { requestId = "measure" }));
        Assert.Equal(1, replies["hello"].GetProperty("result").GetProperty("protocolVersion").GetInt32());
        Assert.Equal(JsonValueKind.Object, replies["cancel"].GetProperty("result").ValueKind);
        var measure = replies["measure"];
        if (measure.TryGetProperty("error", out var error)) Assert.Equal("CANCELED", error.GetProperty("code").GetString());
        else Assert.Equal("4000", measure.GetProperty("result").GetProperty("files").GetString());
    }

    [Fact]
    public async Task A_measurement_past_its_budget_is_a_typed_timeout_but_a_caller_cancel_stays_a_cancel()
    {
        using var directory = new TempDirectory(); Directory.CreateDirectory(directory.Path);
        var slow = new SlowMeasurer(directory.Path);
        var timeout = await Assert.ThrowsAsync<DiscoveryException>(() => Service(directory.Path, TimeSpan.FromMilliseconds(50), slow).MeasureAsync());
        Assert.Equal("STORAGE_MEASURE_TIMEOUT", timeout.Code);
        Assert.True(timeout.Retryable);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(directory.Path, TimeSpan.FromMinutes(5), slow).MeasureAsync(cancel.Token));
    }

    [Fact]
    public async Task The_host_advertises_settings_and_sends_exact_wire_shapes()
    {
        using var directory = new TempDirectory(); Directory.CreateDirectory(directory.Path);
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "payload"), new byte[42]);
        var replies = await Run(Service(directory.Path),
            Request("hello", "hello", new { }), Request("read", "settings.read", new { }),
            Request("measure", "settings.storage.measure", new { }),
            Request("extra", "settings.read", new { scope = "all" }),
            Request("text-value", "settings.set", new { name = SettingsService.CommunityBuildHistory, value = "true" }),
            Request("unknown-name", "settings.set", new { name = "bandwidthCap", value = true }),
            Request("missing-value", "settings.set", new { name = SettingsService.CommunityBuildHistory }));
        Assert.Contains(replies["hello"].GetProperty("result").GetProperty("capabilities").EnumerateArray(), item => item.GetString() == "settings-v1");
        var read = replies["read"].GetProperty("result");
        Assert.Equal(1, read.GetProperty("formatVersion").GetInt32());
        Assert.Equal("absent", read.GetProperty("file").GetString());
        Assert.Equal(JsonValueKind.Null, read.GetProperty("problem").ValueKind);
        Assert.Equal(JsonValueKind.Null, read.GetProperty("lastChangedAt").ValueKind);
        var community = read.GetProperty("communityBuildHistory");
        Assert.True(community.GetProperty("value").GetBoolean());
        Assert.True(community.GetProperty("defaultValue").GetBoolean());
        Assert.Equal("default", community.GetProperty("source").GetString());
        var fetch = read.GetProperty("fetchTableDefinitions");
        Assert.True(fetch.GetProperty("value").GetBoolean());
        Assert.True(fetch.GetProperty("defaultValue").GetBoolean());
        Assert.Equal("default", fetch.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, fetch.GetProperty("problem").ValueKind);
        Assert.Equal(Path.GetFullPath(directory.Path), read.GetProperty("storage").GetProperty("dataDirectory").GetString());
        var limits = read.GetProperty("downloadLimits");
        Assert.Equal(4, limits.GetProperty("parallelDownloads").GetInt32());
        Assert.Equal(2, limits.GetProperty("backgroundDownloads").GetInt32());
        Assert.Equal(JsonValueKind.Null, limits.GetProperty("bandwidthCapBytesPerSecond").ValueKind);
        Assert.False(limits.GetProperty("adjustable").GetBoolean());
        var measured = replies["measure"].GetProperty("result");
        Assert.Equal("42", measured.GetProperty("bytes").GetString());
        Assert.Equal("1", measured.GetProperty("files").GetString());
        Assert.Equal("0", measured.GetProperty("skipped").GetString());
        Assert.EndsWith("+00:00", measured.GetProperty("measuredAt").GetString());
        foreach (var id in new[] { "extra", "text-value", "unknown-name", "missing-value" })
            Assert.Equal("INVALID_REQUEST", replies[id].GetProperty("error").GetProperty("code").GetString());
        var set = await Run(Service(directory.Path), Request("set", "settings.set", new { name = SettingsService.CommunityBuildHistory, value = true }));
        var committed = set["set"].GetProperty("result");
        Assert.Equal("read", committed.GetProperty("file").GetString());
        Assert.True(committed.GetProperty("communityBuildHistory").GetProperty("value").GetBoolean());
        Assert.Equal("saved", committed.GetProperty("communityBuildHistory").GetProperty("source").GetString());
        Assert.EndsWith("+00:00", committed.GetProperty("lastChangedAt").GetString());
        var off = (await Run(Service(directory.Path), Request("off", "settings.set", new { name = SettingsService.FetchTableDefinitions, value = false })))["off"].GetProperty("result");
        Assert.False(off.GetProperty("fetchTableDefinitions").GetProperty("value").GetBoolean());
        Assert.Equal("saved", off.GetProperty("fetchTableDefinitions").GetProperty("source").GetString());
        Assert.True(off.GetProperty("communityBuildHistory").GetProperty("value").GetBoolean());
    }

    [Fact]
    public async Task Without_a_settings_service_the_host_neither_advertises_nor_fabricates_settings()
    {
        var replies = await Run(null, Request("hello", "hello", new { }), Request("read", "settings.read", new { }));
        Assert.DoesNotContain(replies["hello"].GetProperty("result").GetProperty("capabilities").EnumerateArray(), item => item.GetString() == "settings-v1");
        Assert.Equal("SETTINGS_UNAVAILABLE", replies["read"].GetProperty("error").GetProperty("code").GetString());
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static object Request(string id, string method, object parameters) => new { protocolVersion = 1, id, method, @params = parameters };
    // Replies are read before the input closes: the host cancels requests still active at end of input.
    private static async Task<Dictionary<string, JsonElement>> Run(SettingsService? settings, params object[] requests)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var client = new TcpClient(); await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var server = await listener.AcceptTcpClientAsync();
        var completion = new ProtocolHost(new DiscoveryService(new Unused(), new Unused()), settings: settings).RunAsync(server.GetStream(), server.GetStream());
        var stream = client.GetStream();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        foreach (var request in requests) await writer.WriteLineAsync(JsonSerializer.Serialize(request, Json));
        await writer.FlushAsync();
        var replies = new Dictionary<string, JsonElement>();
        while (replies.Count < requests.Length)
        {
            using var document = JsonDocument.Parse((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!);
            var frame = document.RootElement.Clone();
            if (frame.TryGetProperty("id", out var id)) replies[id.GetString()!] = frame;
        }
        client.Client.Shutdown(SocketShutdown.Send);
        await completion.WaitAsync(TimeSpan.FromSeconds(10));
        return replies;
    }
    private sealed class SlowMeasurer(string location) : IStorageMeasurer
    {
        public string Location { get; } = location;
        public async Task<StorageMeasurement> MeasureAsync(CancellationToken token)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException();
        }
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
}
