using System.Net;
using System.Text;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

// #88: when no definition reads an effects table on this build, that source reports a state instead of failing the whole
// effects request. A failed WoWDBDefs fetch can change on Retry; fetching off names the Settings switch; a definition
// too large, unreadable or absent upstream can't change until an update.
[Collection("Items projection builder")]
public sealed class ItemEffectsDefinitionSourceTests
{
    private const string Bridge = "00cb674f", Effect = "4002a5b1", Name = "46c66698";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static TheoryData<string, ItemEffectsSourceState, bool> Kinds => new()
    {
        { "offline", ItemEffectsSourceState.Unavailable, true },
        { "failed", ItemEffectsSourceState.Unavailable, true },
        { "large", ItemEffectsSourceState.Unsupported, false },
        { "invalid", ItemEffectsSourceState.Unsupported, false },
        { "missing", ItemEffectsSourceState.Unsupported, false },
        { "off", ItemEffectsSourceState.Unsupported, false },
    };

    /// <summary>The failure the shared table service throws when no definition reads the table (D081, D101).</summary>
    private static async Task<DiscoveryException> Unusable(string table, string kind, string build)
    {
        var entry = (await TablesCatalog.Provider.Value).Entries.Single(value => value.TableId == table); var name = entry.Name!;
        DiscoveryException? failure = kind switch
        {
            "offline" => WoWDBDefsDefinitions.Offline(name), "failed" => WoWDBDefsDefinitions.Failed(name, "GitHub answered 503."),
            "large" => WoWDBDefsDefinitions.TooLarge(name), "invalid" => WoWDBDefsDefinitions.Invalid(name, "DBD_INVALID"),
            "missing" => WoWDBDefsDefinitions.NotFound(name), _ => null
        };
        return TableDefinitionSources.Unusable(entry, build, null, new(null, null, failure), kind != "off", "Item effects");
    }

    private static ItemEffectsProblem Expected(ItemEffectsSourceState state, string table, string kind) => state == ItemEffectsSourceState.Unavailable
        ? new("ITEM_EFFECTS_SOURCE_UNAVAILABLE", "The selected source is unavailable.", true, new(table, "on"))
        : new("ITEM_EFFECTS_SOURCE_UNSUPPORTED", "The selected source profile is unsupported.", false, new(table, kind == "off" ? "off" : "on"));

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_link_table_no_definition_reads_is_a_source_state_that_names_the_table_and_switch(string kind, ItemEffectsSourceState state, bool retryable)
    {
        await using var f = await ItemEffectsStoreFixture.Create(bridge: false);
        var failure = await Unusable(Bridge, kind, f.H.Opening.Build.Version);
        f.BeforePrepare = (_, _) => throw failure;
        var links = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
        Assert.Equal(Enum.Parse<ItemEffectsReadinessState>(state.ToString()), links.State); Assert.Null(links.Snapshot);
        Assert.Equal(state, links.Source!.State); Assert.Equal(Expected(state, Bridge, kind), links.Problem);
        Assert.Equal(retryable, links.Problem!.Retryable); Assert.Equal(links.Problem, links.Source.Problem);
        ItemEffectsValidation.Validate(links, f.Selection);
        var wire = JsonSerializer.SerializeToElement(links.Problem, Json);
        Assert.Equal(Bridge, wire.GetProperty("definition").GetProperty("tableId").GetString());
        Assert.Equal(kind == "off" ? "off" : "on", wire.GetProperty("definition").GetProperty("definitionFetch").GetString());
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("off")]
    public async Task An_inputs_table_no_definition_reads_keeps_the_links_and_fails_only_its_own_source(string kind)
    {
        await using var f = await ItemEffectsStoreFixture.Create(effect: false, name: false);
        var ready = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
        var page = await f.Store.ReadPageAsync(new(f.Selection, ready.Snapshot!.ArtifactId), default);
        var failure = await Unusable(Effect, kind, f.H.Opening.Build.Version);
        f.BeforePrepare = (_, _) => throw failure;
        var request = new ItemEffectDetailsRequest(f.Selection, ready.Snapshot.ArtifactId, page.Edges.Select(edge => edge.Key).ToArray());
        var details = await f.Store.PrepareDetailsAsync(request, _ => Task.CompletedTask, default);
        var state = kind == "off" ? ItemEffectsSourceState.Unsupported : ItemEffectsSourceState.Unavailable;
        Assert.Equal(state, details.Source.State); Assert.Equal(Expected(state, Effect, kind), details.Source.Problem);
        Assert.All(details.Outcomes, outcome => Assert.True(outcome.State == Enum.Parse<ItemEffectState>(state.ToString()) &&
            outcome.Problem == details.Source.Problem || outcome.State == ItemEffectState.EffectKeyOutOfRange));
        ItemEffectsValidation.Validate(details, request);
        Assert.Equal(ItemEffectsReadinessState.Ready, (await f.Store.ReadAsync(f.Selection, default)).State);
    }

    // The rest of the request still answers: the Effect source stays Ready next to a Name source that failed its fetch.
    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_names_table_no_definition_reads_leaves_the_ready_effect_source_and_its_evidence(string kind, ItemEffectsSourceState state, bool retryable)
    {
        await using var f = await ItemEffectsStoreFixture.Create(name: false);
        var ready = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
        var keys = (await f.Store.ReadPageAsync(new(f.Selection, ready.Snapshot!.ArtifactId), default)).Edges.Select(edge => edge.Key).ToArray();
        var details = await f.Store.ReadDetailsAsync(new(f.Selection, ready.Snapshot.ArtifactId, keys), default);
        var failure = await Unusable(Name, kind, f.H.Opening.Build.Version);
        f.BeforePrepare = (_, _) => throw failure;
        var request = new ItemEffectNamesRequest(f.Selection, ready.Snapshot.ArtifactId, details.Source.Snapshot!.ArtifactId, keys);
        var names = await f.Store.PrepareNamesAsync(request, _ => Task.CompletedTask, default);
        Assert.Equal(ItemEffectsSourceState.Ready, names.Sources[0].State); Assert.Equal(details.Source.Snapshot.ArtifactId, names.Sources[0].Snapshot!.ArtifactId);
        Assert.Equal(state, names.Sources[1].State); Assert.Equal(Expected(state, Name, kind), names.Sources[1].Problem);
        Assert.Equal(retryable, names.Sources[1].Problem!.Retryable);
        var named = names.Outcomes.Where(outcome => outcome.State != ItemEffectNameState.EffectNotReady && outcome.State != ItemEffectNameState.SpellKeyOutOfRange).ToArray();
        Assert.NotEmpty(named);
        Assert.All(named, outcome => { Assert.Equal(Enum.Parse<ItemEffectNameState>(state.ToString()), outcome.State); Assert.Equal(names.Sources[1].Problem, outcome.Problem); });
        Assert.Contains(names.Outcomes, outcome => outcome.Effect.State == ItemEffectState.ObservedReady);
        ItemEffectsValidation.Validate(names, request);
        Assert.Equal(new[] { Name }, f.Prepared.ToArray());
    }

    // Any other failure keeps its fixed problem with no definition, and a definition naming another table is dropped.
    [Fact]
    public async Task Only_a_definition_failure_for_the_table_the_role_reads_carries_a_definition()
    {
        await using var f = await ItemEffectsStoreFixture.Create(bridge: false);
        var other = await Unusable(Name, "offline", f.H.Opening.Build.Version);
        f.BeforePrepare = (_, _) => throw other;
        var links = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
        Assert.Equal(new ItemEffectsProblem("ITEM_EFFECTS_SOURCE_UNAVAILABLE", "The selected source is unavailable.", true), links.Problem);
        Assert.False(JsonSerializer.SerializeToElement(links.Problem, Json).TryGetProperty("definition", out _));
    }

    // #85 through the real shared table service: the definition choice runs for real, then the exact build's ROOT lists no
    // ItemXItemEffect file, so the link table is notInBuild rather than a failure.
    [Fact]
    public async Task A_link_table_the_ROOT_does_not_list_is_not_in_build_through_the_real_table_prepare()
    {
        await using var f = await ItemEffectsStoreFixture.Create(bridge: false, realTables: true);
        var links = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
        Assert.Equal(ItemEffectsReadinessState.NotInBuild, links.State);
        Assert.Equal(new ItemEffectsProblem("ITEM_EFFECTS_SOURCE_NOT_IN_BUILD", "This build does not include the selected source.", false), links.Problem);
        Assert.Equal(new[] { Bridge }, f.Prepared.ToArray());
        // The verified local ROOT decided it; nothing was downloaded.
        Assert.Empty(f.Cdn!.Requests);
        ItemEffectsValidation.Validate(links, f.Selection);
    }

    private const string Unlisted = "99.0.0.1", Commit = "3333333333333333333333333333333333333333";

    /// <summary>A build no bundled definition covers (D081, D101): Items reads with fetched Item and ItemSparse definitions,
    /// and every effects prepare goes through the real shared table service and its definition choice.</summary>
    private static Task<ItemEffectsStoreFixture> UnlistedBuild(FetchedDefinitionTests.GitHub github) =>
        ItemEffectsStoreFixture.Create(bridge: false, effect: false, name: false, version: Unlisted, realTables: true,
            definitions: async directory =>
            {
                var fetched = new WoWDBDefsDefinitions(directory, new HttpClient(github), new FetchedDefinitionTests.Clock());
                foreach (var (table, name, layout) in new[] { (ItemsProjection.ItemTable, "Item", "996192AA"), (ItemsProjection.SparseTable, "ItemSparse", "1C17D17F") })
                {
                    github.Serve(name, Commit, ItemsStoreTests.WithBuild(name, layout, Unlisted));
                    Assert.NotNull((await fetched.EnsureAsync(table, name, _ => true, _ => Task.CompletedTask, default)).Definition);
                }
                github.Routes.Clear();
                return new TableDefinitionSources(fetched, new FetchedDefinitionTests.Policy(true));
            });

    // ItemXItemEffect's own fetch fails, and the link source says so with the right Retry.
    [Theory]
    [InlineData("offline", ItemEffectsReadinessState.Unavailable, true)]
    [InlineData("failed", ItemEffectsReadinessState.Unavailable, true)]
    [InlineData("large", ItemEffectsReadinessState.Unsupported, false)]
    [InlineData("invalid", ItemEffectsReadinessState.Unsupported, false)]
    [InlineData("missing", ItemEffectsReadinessState.Unsupported, false)]
    public async Task A_failed_fetch_through_the_real_table_prepare_is_a_link_source_state(string kind, ItemEffectsReadinessState expected, bool retryable)
    {
        var github = new FetchedDefinitionTests.GitHub();
        await using var f = await UnlistedBuild(github);
        if (kind == "offline") github.Failure = new HttpRequestException(HttpRequestError.ConnectionError, "offline");
        if (kind == "failed") github.Routes[WoWDBDefsDefinitions.CommitsUrl("ItemXItemEffect").AbsoluteUri] = () => new(HttpStatusCode.ServiceUnavailable);
        if (kind == "large") github.Serve("ItemXItemEffect", Commit, new byte[WoWDBDefsDefinitions.MaxDefinitionBytes + 1]);
        if (kind == "invalid") github.Serve("ItemXItemEffect", Commit, "COLUMNS\n"u8.ToArray());
        var asked = github.Requests.Count;
        var links = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
        Assert.Equal(expected, links.State); Assert.Equal(retryable, links.Problem!.Retryable);
        Assert.Equal(new TableDefinitionProblem(Bridge, "on"), links.Problem.Definition); Assert.Equal(links.Problem, links.Source!.Problem);
        Assert.Equal("itemXItemEffect", links.Link); Assert.Equal(new[] { Bridge }, f.Prepared.ToArray());
        // WoWDBDefs was asked; nothing was downloaded from the CDN.
        Assert.True(github.Requests.Count > asked); Assert.Empty(f.Cdn!.Requests);
        ItemEffectsValidation.Validate(links, f.Selection);
        // Back online, Retry asks again at once and reports what it finds, still as a source state.
        if (retryable)
        {
            github.Failure = null; github.Routes.Clear();
            var again = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
            Assert.Equal(ItemEffectsReadinessState.Unsupported, again.State);
            Assert.Equal(new TableDefinitionProblem(Bridge, "on"), again.Problem!.Definition);
        }
    }

    // #85 with a fetched definition: WoWDBDefs lists the build, so the shared choice reads with it, and then the ROOT lists
    // no ItemXItemEffect file. The table is not in the build, not a definition problem.
    [Fact]
    public async Task A_link_table_a_fetched_definition_lists_but_the_ROOT_does_not_is_not_in_build()
    {
        var github = new FetchedDefinitionTests.GitHub();
        await using var f = await UnlistedBuild(github);
        github.Serve("ItemXItemEffect", Commit, ItemsStoreTests.WithBuild("ItemXItemEffect", "96F083AD", Unlisted));
        var links = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
        Assert.Equal(ItemEffectsReadinessState.NotInBuild, links.State);
        Assert.Equal(new ItemEffectsProblem("ITEM_EFFECTS_SOURCE_NOT_IN_BUILD", "This build does not include the selected source.", false), links.Problem);
        Assert.Contains(github.Requests, uri => uri.AbsoluteUri == WoWDBDefsDefinitions.DefinitionUrl(Commit, "ItemXItemEffect"));
        Assert.Empty(f.Cdn!.Requests);
        ItemEffectsValidation.Validate(links, f.Selection);
    }
}
