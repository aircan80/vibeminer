using System.Text;
using System.Text.Json;
using VibeMiner.Builds.Testing;
using Xunit;
using static VibeMiner.Builds.Tests.BuildRegistryTestKit;

namespace VibeMiner.Builds.Tests;

public abstract class BuildRegistryContractTests : IAsyncLifetime
{
    protected IBuildRegistry Registry { get; private set; } = null!;
    protected abstract Task<IBuildRegistry> CreateAsync();
    protected virtual Task CloseAsync() => Registry.DisposeAsync().AsTask();
    public async Task InitializeAsync() => Registry = await CreateAsync();
    public Task DisposeAsync() => CloseAsync();
    internal static readonly DateTimeOffset T0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    internal static BuildRegistryObservation Observation(int seed = 1) { var identity = Identity(seed); return new(identity, Route(identity), "enUS", T0); }
    private static async Task AssertCodeAsync(string code, Func<Task> action) => Assert.Equal(code, (await Assert.ThrowsAsync<BuildRegistryException>(action)).Code);

    [Theory]
    [InlineData(BuildLoadState.Loaded)]
    [InlineData(BuildLoadState.Failed)]
    [InlineData(BuildLoadState.Interrupted)]
    public async Task ObservedBuildTransitionsThroughLoadingToExactlyOneTerminalOutcome(BuildLoadState terminal)
    {
        var observation = Observation(); var discovered = await Registry.ObserveAsync(observation);
        Assert.Equal(BuildLoadState.Discovered, discovered.State); Assert.Equal(0, discovered.Generation);
        Assert.Null(discovered.AttemptBuild); Assert.Null(discovered.AttemptLocale); Assert.Null(discovered.FinishedAt);
        var attempt = await Registry.BeginLoadAsync(observation.Identity.Id, observation.Selection, "enUS");
        var loading = Assert.IsType<BuildRegistryEntry>(await Registry.GetAsync(observation.Identity.Id));
        Assert.Equal(BuildLoadState.Loading, loading.State); Assert.Equal(1, loading.Generation); Assert.Equal(1, attempt.Generation);
        Assert.Equal(observation.Selection, loading.AttemptBuild); Assert.Equal("enUS", loading.AttemptLocale); Assert.NotNull(loading.AttemptStartedAt);
        var diagnostic = new BuildRegistryDiagnostic("AUTHORED_RESULT", "metadata outcome");
        var finished = await Registry.FinishAsync(attempt, terminal, diagnostic);
        Assert.Equal(terminal, finished.State); Assert.Equal(1, finished.Generation); Assert.Equal(diagnostic, finished.Diagnostic); Assert.NotNull(finished.FinishedAt);
        await AssertCodeAsync(BuildRegistryCodes.Stale, () => Registry.FinishAsync(attempt, terminal));
        var reread = Assert.IsType<BuildRegistryEntry>(await Registry.GetAsync(observation.Identity.Id));
        Assert.Equal(finished.State, reread.State); Assert.Equal(finished.Generation, reread.Generation);
        Assert.Equal(finished.AttemptBuild, reread.AttemptBuild); Assert.Equal(finished.AttemptLocale, reread.AttemptLocale);
        Assert.Equal(finished.Diagnostic, reread.Diagnostic); Assert.Equal(finished.FinishedAt, reread.FinishedAt);
    }

    [Fact]
    public async Task RouteObservationsNeverAlterCapturedAttemptOrTerminalStateAndTimestamps()
    {
        var first = Observation(); await Registry.ObserveAsync(first);
        var attempt = await Registry.BeginLoadAsync(first.Identity.Id, first.Selection, "enUS");
        var loading = Assert.IsType<BuildRegistryEntry>(await Registry.GetAsync(first.Identity.Id));
        var latest = first with { Selection = Route(first.Identity, "eu", 'd'), Locale = "frFR", ObservedAt = T0.AddHours(2) };
        var updated = await Registry.ObserveAsync(latest);
        Assert.Equal(BuildLoadState.Loading, updated.State); Assert.Equal(loading.Generation, updated.Generation); Assert.Equal(loading.UpdatedAt, updated.UpdatedAt);
        Assert.Equal(latest.Selection, updated.ObservedBuild); Assert.Equal("frFR", updated.ObservedLocale);
        Assert.Equal(first.Selection, updated.AttemptBuild); Assert.Equal("enUS", updated.AttemptLocale);
        var finished = await Registry.FinishAsync(attempt, BuildLoadState.Loaded);
        var older = await Registry.ObserveAsync(first with { ObservedAt = T0.AddHours(-1), Locale = "deDE" });
        Assert.Equal(T0.AddHours(-1), older.FirstObservedAt); Assert.Equal(latest.ObservedAt, older.LastObservedAt);
        Assert.Equal(latest.Selection, older.ObservedBuild); Assert.Equal("frFR", older.ObservedLocale);
        Assert.Equal(finished.UpdatedAt, older.UpdatedAt); Assert.Equal(finished.FinishedAt, older.FinishedAt); Assert.Equal(finished.AttemptStartedAt, older.AttemptStartedAt);
        Assert.Equal(finished.AttemptBuild, older.AttemptBuild); Assert.Equal(BuildLoadState.Loaded, older.State);
        var tie = await Registry.ObserveAsync(latest with { Selection = Route(first.Identity, "kr", 'e'), Locale = "koKR" });
        Assert.Equal("kr", tie.ObservedBuild.Region); Assert.Equal("koKR", tie.ObservedLocale); Assert.Equal(1, tie.Generation);
    }

    [Fact]
    public async Task NewAttemptsSupersedeLoadingAndTerminalAttemptsAndFenceTheirTokens()
    {
        var observation = Observation(); await Registry.ObserveAsync(observation);
        var first = await Registry.BeginLoadAsync(observation.Identity.Id, observation.Selection, "enUS");
        var secondRoute = Route(observation.Identity, "eu", 'd');
        var second = await Registry.BeginLoadAsync(observation.Identity.Id, secondRoute, "deDE");
        Assert.Equal(2, second.Generation); await AssertCodeAsync(BuildRegistryCodes.Stale, () => Registry.FinishAsync(first, BuildLoadState.Failed));
        var failed = await Registry.FinishAsync(second, BuildLoadState.Failed, new("AUTHORED_FAILURE", "retry allowed"));
        var third = await Registry.BeginLoadAsync(observation.Identity.Id, observation.Selection, "frFR");
        Assert.Equal(3, third.Generation);
        var loading = Assert.IsType<BuildRegistryEntry>(await Registry.GetAsync(observation.Identity.Id));
        Assert.Null(loading.FinishedAt); Assert.Null(loading.Diagnostic); Assert.Equal("frFR", loading.AttemptLocale);
        Assert.Equal(failed.LastObservedAt, loading.LastObservedAt); Assert.Equal(observation.Selection, loading.ObservedBuild);
        await AssertCodeAsync(BuildRegistryCodes.Stale, () => Registry.FinishAsync(second, BuildLoadState.Loaded));
        await Registry.FinishAsync(third, BuildLoadState.Interrupted);
    }

    [Fact]
    public async Task IdentityIsRecomputedDefensivelyAndSameVersionNeverCollapsesDifferentBuilds()
    {
        var identity = Identity(); var mutable = identity.Fields.ToDictionary(pair => pair.Key, pair => pair.Value);
        var supplied = identity with { Fields = mutable };
        await Registry.ObserveAsync(new(supplied, Route(identity), "enUS", T0));
        mutable["VersionsName"] = "caller changed this"; mutable["Extra"] = "not observed";
        var saved = Assert.IsType<BuildRegistryEntry>(await Registry.GetAsync(identity.Id));
        Assert.Equal(identity.Canonical, saved.Identity.Canonical); Assert.Equal(identity.Fields, saved.Identity.Fields);
        var second = Identity(2); await Registry.ObserveAsync(new(second, Route(second), "enUS", T0));
        Assert.Equal(identity.Fields["VersionsName"], second.Fields["VersionsName"]); Assert.NotEqual(identity.Id, second.Id);
        Assert.Equal(2, (await Registry.ListAsync()).Entries.Count);
        foreach (var bad in new[] { identity with { Id = new string('0', 64) }, identity with { Canonical = identity.Canonical + "x" },
                     identity with { ProductId = "wowdev" }, identity with { Fields = new Dictionary<string, string>(identity.Fields) { ["BuildConfig"] = new string('f', 32) } } })
            await AssertCodeAsync(BuildRegistryCodes.Validation, () => Registry.ObserveAsync(new(bad, Route(identity), "enUS", T0)));
        Assert.Equal(2, (await Registry.ListAsync()).Entries.Count);
    }

    [Theory]
    [InlineData("enus")]
    [InlineData(" enUS")]
    [InlineData("enUS ")]
    [InlineData("unknown")]
    [InlineData("")]
    public async Task LocaleMustBeAnExactCanonicalLocaleAndValidationNeverAdvancesState(string locale)
    {
        var observation = Observation(); await Registry.ObserveAsync(observation);
        await AssertCodeAsync(BuildRegistryCodes.Validation, () => Registry.ObserveAsync(observation with { Locale = locale }));
        await AssertCodeAsync(BuildRegistryCodes.Validation, () => Registry.BeginLoadAsync(observation.Identity.Id, observation.Selection, locale));
        Assert.Equal(0, (await Registry.GetAsync(observation.Identity.Id))!.Generation);
    }

    [Fact]
    public async Task BuildRefMismatchBoundsAndInvalidOutcomesHaveExplicitFailuresWithoutMutation()
    {
        var observation = Observation(); await Registry.ObserveAsync(observation);
        foreach (var route in new[] { new BuildRef("wowdev", observation.Selection.BuildConfig, observation.Selection.CdnConfig), new BuildRef("wow", new string('f', 32), observation.Selection.CdnConfig) })
        {
            await AssertCodeAsync(BuildRegistryCodes.Validation, () => Registry.ObserveAsync(observation with { Selection = route }));
            await AssertCodeAsync(BuildRegistryCodes.Validation, () => Registry.BeginLoadAsync(observation.Identity.Id, route, "enUS"));
        }
        await AssertCodeAsync(BuildRegistryCodes.Missing, () => Registry.BeginLoadAsync(Identity(99).Id, Route(Identity(99)), "enUS"));
        await AssertCodeAsync(BuildRegistryCodes.Validation, () => Registry.GetAsync("authored-version"));
        var attempt = await Registry.BeginLoadAsync(observation.Identity.Id, observation.Selection, "enUS");
        foreach (var state in new[] { BuildLoadState.Discovered, BuildLoadState.Loading, (BuildLoadState)99 })
            await AssertCodeAsync(BuildRegistryCodes.Validation, () => Registry.FinishAsync(attempt, state));
        foreach (var diagnostic in new[] { new BuildRegistryDiagnostic(new string('x', 129), "m"), new BuildRegistryDiagnostic("code", new string('m', 4097)), new BuildRegistryDiagnostic("bad\0code", "m") })
            await AssertCodeAsync(BuildRegistryCodes.Validation, () => Registry.FinishAsync(attempt, BuildLoadState.Failed, diagnostic));
        Assert.Equal(BuildLoadState.Loading, (await Registry.GetAsync(observation.Identity.Id))!.State);
        await Registry.FinishAsync(attempt, BuildLoadState.Loaded, new(new string('x', 128), new string('m', 4096)));
    }

    [Fact]
    public async Task IdentityFieldAndUtf8BoundsAreValidatedBeforeAnyEntryIsWritten()
    {
        var original = Identity();
        var tooMany = original.Fields.ToDictionary(pair => pair.Key, pair => pair.Value);
        for (var i = 0; i < 65; i++) tooMany[$"field-{i}"] = "value";
        var longName = original.Fields.ToDictionary(pair => pair.Key, pair => pair.Value); longName[new string('n', 129)] = "value";
        var longValue = original.Fields.ToDictionary(pair => pair.Key, pair => pair.Value); longValue["field"] = new string('é', 1025);
        var oversized = original.Fields.ToDictionary(pair => pair.Key, pair => pair.Value);
        for (var i = 0; i < 20; i++) oversized[$"field-{i}"] = new string('x', 2000);
        var nul = original.Fields.ToDictionary(pair => pair.Key, pair => pair.Value); nul["bad\0name"] = "value";
        foreach (var fields in new[] { tooMany, longName, longValue, oversized, nul })
        {
            var identity = BuildIdentity.FromFields("wow", fields);
            await AssertCodeAsync(BuildRegistryCodes.Validation, () => Registry.ObserveAsync(new(identity, Route(identity), "enUS", T0)));
        }
        Assert.Empty((await Registry.ListAsync()).Entries);
        await Registry.ObserveAsync(Observation());
    }

    [Fact]
    public async Task KeysetPagesAreBoundedOrderedExactAndUnknownIdsAreAbsent()
    {
        var identities = Enumerable.Range(1, 1031).Select(seed => Identity(seed)).ToArray();
        foreach (var identity in identities.Reverse()) await Registry.ObserveAsync(new(identity, Route(identity), "enUS", T0));
        var found = new List<string>(); BuildRegistryCursor? cursor = null; var pages = 0;
        do
        {
            var page = await Registry.ListAsync(1024, cursor); Assert.InRange(page.Entries.Count, 1, 1024);
            found.AddRange(page.Entries.Select(entry => entry.Identity.Id)); cursor = page.Next;
            Assert.True(++pages <= 2, "Small authored entries exceeded their bounded two-page traversal.");
        } while (cursor is not null);
        Assert.Equal(identities.Select(identity => identity.Id).Order(StringComparer.Ordinal), found);
        Assert.Null(await Registry.GetAsync(Identity(99999).Id));
        await AssertCodeAsync(BuildRegistryCodes.Validation, () => Registry.ListAsync(0));
        await AssertCodeAsync(BuildRegistryCodes.Validation, () => Registry.ListAsync(1025));
    }

    [Fact]
    public async Task PageByteBoundCanShortenAPageBelowItsEntryCountLimit()
    {
        for (var seed = 1; seed <= 64; seed++)
        {
            var fields = Identity(seed).Fields.ToDictionary(pair => pair.Key, pair => pair.Value);
            for (var field = 0; field < 14; field++) fields[$"authored-{field}"] = new string('é', 1024);
            var identity = BuildIdentity.FromFields("wow", fields);
            await Registry.ObserveAsync(new(identity, Route(identity), "enUS", T0));
        }
        var page = await Registry.ListAsync(1024);
        Assert.NotNull(page.Next); Assert.InRange(page.Entries.Count, 1, 63);
        Assert.True(page.Entries.Sum(entry => (long)JsonSerializer.SerializeToUtf8Bytes(entry).Length) <= BuildRegistryLimits.MaxPageBytes);
        var next = await Registry.ListAsync(1024, page.Next); Assert.NotEmpty(next.Entries);
        Assert.DoesNotContain(next.Entries, entry => page.Entries.Any(previous => previous.Identity.Id == entry.Identity.Id));
    }

    [Fact]
    public async Task ConcurrentBeginsHaveDistinctGenerationsAndOnlyTheLatestCanFinish()
    {
        var observation = Observation(); await Registry.ObserveAsync(observation);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Registry.BeginLoadAsync(observation.Identity.Id, observation.Selection, "enUS")));
        Assert.Equal(Enumerable.Range(1, 16).Select(value => (long)value), attempts.Select(attempt => attempt.Generation).Order());
        foreach (var stale in attempts.Where(attempt => attempt.Generation != 16)) await AssertCodeAsync(BuildRegistryCodes.Stale, () => Registry.FinishAsync(stale, BuildLoadState.Loaded));
        await Registry.FinishAsync(Assert.Single(attempts, attempt => attempt.Generation == 16), BuildLoadState.Loaded);
    }

    [Fact]
    public async Task PreCancellationChangesNothingAndCurrentAttemptCanStillFinishAfterRetry()
    {
        var observation = Observation(); using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Registry.ObserveAsync(observation, canceled.Token));
        Assert.Null(await Registry.GetAsync(observation.Identity.Id)); await Registry.ObserveAsync(observation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Registry.BeginLoadAsync(observation.Identity.Id, observation.Selection, "enUS", canceled.Token));
        var attempt = await Registry.BeginLoadAsync(observation.Identity.Id, observation.Selection, "enUS"); Assert.Equal(1, attempt.Generation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Registry.FinishAsync(attempt, BuildLoadState.Loaded, ct: canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Registry.GetAsync(observation.Identity.Id, canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Registry.ListAsync(ct: canceled.Token));
        Assert.Equal(BuildLoadState.Loading, (await Registry.GetAsync(observation.Identity.Id))!.State); await Registry.FinishAsync(attempt, BuildLoadState.Loaded);
    }

    [Fact]
    public async Task ClosedRegistryRejectsNewCallsAndRepeatedDisposeIsSafe()
    {
        var observation = Observation(); await Registry.ObserveAsync(observation); var attempt = await Registry.BeginLoadAsync(observation.Identity.Id, observation.Selection, "enUS");
        await Registry.DisposeAsync(); await Registry.DisposeAsync();
        await AssertCodeAsync(BuildRegistryCodes.Closed, () => Registry.ObserveAsync(observation));
        await AssertCodeAsync(BuildRegistryCodes.Closed, () => Registry.BeginLoadAsync(observation.Identity.Id, observation.Selection, "enUS"));
        await AssertCodeAsync(BuildRegistryCodes.Closed, () => Registry.FinishAsync(attempt, BuildLoadState.Loaded));
        await AssertCodeAsync(BuildRegistryCodes.Closed, () => Registry.GetAsync(observation.Identity.Id));
        await AssertCodeAsync(BuildRegistryCodes.Closed, () => Registry.ListAsync());
    }
}

public sealed class BuildRegistryContractOnRealStore : BuildRegistryContractTests
{
    private readonly RegistryDirectory directory = new();
    protected override async Task<IBuildRegistry> CreateAsync() => await BuildRegistry.OpenAsync(directory.Path);
    protected override async Task CloseAsync() { await Registry.DisposeAsync(); directory.Dispose(); }
}
public sealed class BuildRegistryContractOnFake : BuildRegistryContractTests
{
    protected override Task<IBuildRegistry> CreateAsync() => Task.FromResult<IBuildRegistry>(new InMemoryBuildRegistry());
}
