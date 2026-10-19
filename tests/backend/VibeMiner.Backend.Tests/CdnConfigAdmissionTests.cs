using Microsoft.Data.Sqlite;
using VibeMiner.Domain;
using Xunit;

namespace VibeMiner.Backend.Tests;

/// <summary>A republished CDN configuration changes the build in place; admissions keep the one they were made with.</summary>
public sealed class CdnConfigAdmissionTests
{
    [Fact]
    public async Task OpenBuildKeepsItsCdnConfigUntilAnExplicitReopenUsesTheNewestOne()
    {
        await using var fixture = await WorkspaceTests.OpeningFixture.CreateAsync();
        var first = await fixture.Service.OpenAsync(fixture.Request);
        Assert.Equal(fixture.CdnKey, first.Build.CdnConfig);
        Assert.Equal(fixture.CdnKey, first.Configurations.Single(receipt => receipt.Role == "cdnConfig").Key);

        fixture.Clock.Now = fixture.Clock.Now.AddHours(1);
        var page = await fixture.RepublishAsync();
        var build = Assert.Single(page.Builds, value => value.Id == fixture.Request.BuildIdentity);
        Assert.Equal(fixture.RepublishedCdnKey, build.CdnConfig);
        Assert.Equal(new[] { fixture.RepublishedCdnKey, fixture.CdnKey }, build.CdnConfigHistory.Select(entry => entry.CdnConfig));

        // Features on the open build keep resolving the CDN configuration its admission recorded.
        var open = await fixture.Catalog.ResolveOpeningAsync(fixture.Request, default);
        Assert.Equal(fixture.CdnKey, open.Build.CdnConfig);
        Assert.Equal(first.SourceReference, open.Reference);
        Assert.True(await fixture.Catalog.HasAdmissionAsync(open, default));

        // A new open sees the newest one. The older admission does not admit it, and is not an integrity failure.
        var latest = await fixture.Catalog.ResolveLatestOpeningAsync(fixture.Request, default);
        Assert.Equal(fixture.RepublishedCdnKey, latest.Build.CdnConfig);
        Assert.NotEqual(first.SourceReference, latest.Reference);
        Assert.False(await fixture.Catalog.HasAdmissionAsync(latest, default));

        // The explicit restart downloads only the new CDN configuration and reuses the retained build configuration.
        var calls = fixture.Calls.Count;
        var second = await fixture.Service.OpenAsync(fixture.Request);
        Assert.Equal(fixture.RepublishedCdnKey, Assert.Single(fixture.Calls.Skip(calls)).Split('/')[^1]);
        Assert.Equal(fixture.RepublishedCdnKey, second.Build.CdnConfig);
        Assert.Equal(fixture.RepublishedCdnKey, second.Configurations.Single(receipt => receipt.Role == "cdnConfig").Key);
        Assert.Equal(first.Configurations.Single(receipt => receipt.Role == "buildConfig").RetrievedAt,
            second.Configurations.Single(receipt => receipt.Role == "buildConfig").RetrievedAt);
        Assert.Equal(latest.Reference, second.SourceReference);

        // Each admission keeps its own CDN configuration.
        Assert.Equal(2L, fixture.Scalar("SELECT count(*) FROM workspace_admissions"));
        Assert.Equal(1L, fixture.Scalar($"SELECT count(*) FROM workspace_configurations WHERE admission_id=1 AND role='cdnConfig' AND key='{fixture.CdnKey}'"));
        Assert.Equal(1L, fixture.Scalar($"SELECT count(*) FROM workspace_configurations WHERE admission_id=2 AND role='cdnConfig' AND key='{fixture.RepublishedCdnKey}'"));
        Assert.Equal(fixture.RepublishedCdnKey, (await fixture.Catalog.ResolveOpeningAsync(fixture.Request, default)).Build.CdnConfig);

        // Reopening the earlier config's admission context still finds its own proof.
        Assert.True(await fixture.Catalog.HasAdmissionAsync(open, default));
    }

    [Fact]
    public async Task AdmissionCommitsCompareAgainstTheCapturedCdnConfigNotTheNewestOne()
    {
        await using var fixture = await WorkspaceTests.OpeningFixture.CreateAsync();
        var first = await fixture.Service.OpenAsync(fixture.Request);
        var pinned = await fixture.Catalog.ResolveOpeningAsync(fixture.Request, default);
        await fixture.RepublishAsync();
        var second = await fixture.Service.OpenAsync(fixture.Request);
        var latest = await fixture.Catalog.ResolveLatestOpeningAsync(fixture.Request, default);

        // Receipts for one CDN configuration never admit a context captured with the other one.
        Assert.Equal("CONFIGURATION_INCOMPLETE", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            fixture.Catalog.CommitWorkspaceAsync(pinned, second.Configurations, fixture.Clock.Now.ToString("O"), default))).Code);
        Assert.Equal("CONFIGURATION_INCOMPLETE", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            fixture.Catalog.CommitWorkspaceAsync(latest, first.Configurations, fixture.Clock.Now.ToString("O"), default))).Code);
        var problem = new DownloadProblem("DOWNLOAD_MANIFEST_ABSENT", "No download manifest.");
        Assert.Equal("DOWNLOAD_CATALOG_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            fixture.Catalog.CommitUnavailableDownloadWorkspaceAsync(pinned, second.Configurations, problem, default))).Code);
        Assert.Equal("DOWNLOAD_CATALOG_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            fixture.Catalog.CommitUnavailableDownloadWorkspaceAsync(latest, first.Configurations, problem, default))).Code);
        Assert.Equal(2L, fixture.Scalar("SELECT count(*) FROM workspace_admissions"));

        // A download admission that started with the earlier CDN configuration finishes with it, even though a
        // newer one is advertised by now, and the build it reports still shows the newest one first in its history.
        var download = await fixture.Catalog.CommitUnavailableDownloadWorkspaceAsync(pinned, first.Configurations, problem, default);
        Assert.Equal(fixture.CdnKey, download.Build.CdnConfig);
        Assert.Equal(fixture.RepublishedCdnKey, download.Build.CdnConfigHistory[0].CdnConfig);
        Assert.Equal(pinned.Reference, download.SourceReference);
        Assert.Equal(fixture.CdnKey, (await fixture.Catalog.ResolveOpeningAsync(fixture.Request, default)).Build.CdnConfig);
    }

    [Fact]
    public async Task PinnedSourceReferenceKeepsItsOwnCdnConfigAndRefusesATamperedObservation()
    {
        await using var fixture = await WorkspaceTests.OpeningFixture.CreateAsync();
        var first = await fixture.Service.OpenAsync(fixture.Request);
        await fixture.RepublishAsync();
        await fixture.Service.OpenAsync(fixture.Request);

        // Retention plans and running downloads resolve their pinned observation, never the newest CDN configuration.
        var pinned = await fixture.Catalog.ResolveOpeningAsync(fixture.Request, first.SourceReference, default);
        Assert.Equal(fixture.CdnKey, pinned.Build.CdnConfig);
        Assert.Equal(fixture.CdnKey, Assert.Single(pinned.Build.CdnConfigHistory).CdnConfig);
        Assert.Equal(first.SourceReference, pinned.Reference);

        using (var db = new SqliteConnection($"Data Source={Path.Combine(fixture.Directory.Path, "discovery-v1.sqlite3")};Pooling=False"))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = $"UPDATE observations SET cdn_config='{fixture.RepublishedCdnKey}' WHERE generation={first.SourceReference.Generation} AND ordinal={first.SourceReference.VersionsRowOrdinal}";
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        Assert.Equal("CATALOG_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            fixture.Catalog.ResolveOpeningAsync(fixture.Request, first.SourceReference, default))).Code);
    }
}
