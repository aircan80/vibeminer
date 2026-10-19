using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Host;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class ItemClassificationsHostTests
{
    private static readonly ItemsContext Context = new("wow", new string('a', 64), "us", "enUS");
    private static readonly string Feature = new('b', 64);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static object Request(string id, string method, object parameters) => new { protocolVersion = 1, id, method, @params = parameters };
    private static Dictionary<string, object?> Scope(params string[] ids) => new()
    {
        ["productId"] = Context.ProductId, ["buildIdentity"] = Context.BuildIdentity,
        ["sourceRegion"] = Context.SourceRegion, ["researchLocale"] = Context.ResearchLocale,
        ["artifactId"] = Feature, ["itemIds"] = ids
    };

    [Fact]
    public async Task Authored_labels_preserve_exact_evidence_order_duplicates_and_explicit_unsupported_subclass()
    {
        var store = new Store(); await using var labels = new ItemClassificationsService(store);
        var parameters = Scope("7", "0", "4294967295", "7"); parameters["includeSubclass"] = true;
        var frames = await Run(labels, Request("hello", "hello", new { }), Request("batch", "items.classifications", parameters));
        Assert.Contains(Reply(frames, "hello").GetProperty("result").GetProperty("capabilities").EnumerateArray(), value => value.GetString() == "item-classifications-v1");
        var progress = Assert.Single(frames, value => value.TryGetProperty("type", out _));
        Assert.Equal("batch", progress.GetProperty("requestId").GetString()); Assert.Equal(1, progress.GetProperty("sequence").GetInt32());
        Assert.Equal(4, progress.GetProperty("completed").GetInt32()); Assert.Equal(4, progress.GetProperty("total").GetInt32());
        Assert.Equal("rows", progress.GetProperty("unit").GetString());
        var result = Reply(frames, "batch").GetProperty("result");
        Assert.Equal("classification-label-index-v1", result.GetProperty("policyRevision").GetString());
        var sources = result.GetProperty("sources");
        Assert.Equal("class", sources[0].GetProperty("kind").GetString()); Assert.Equal("ready", sources[0].GetProperty("state").GetString());
        Assert.Equal("b977271e", sources[0].GetProperty("snapshot").GetProperty("tableId").GetString());
        Assert.Equal("unsupported", sources[1].GetProperty("state").GetString());
        var outcomes = result.GetProperty("outcomes").EnumerateArray().ToArray();
        Assert.Equal(new[] { "7", "0", "4294967295", "7" }, outcomes.Select(value => value.GetProperty("itemId").GetString()));
        Assert.Equal(new[] { "ready", "emptyText", "missingItem", "ready" }, outcomes.Select(value => value.GetProperty("class").GetProperty("state").GetString()));
        Assert.Equal(" Authored <label> ", outcomes[0].GetProperty("class").GetProperty("text").GetString());
        Assert.True(outcomes[0].GetProperty("class").GetProperty("target").GetProperty("isCopy").GetBoolean());
        Assert.Equal("3", outcomes[0].GetProperty("class").GetProperty("target").GetProperty("copySourceId").GetString());
        Assert.Equal("", outcomes[1].GetProperty("class").GetProperty("text").GetString());
        Assert.Equal("7", outcomes[1].GetProperty("class").GetProperty("target").GetProperty("rowId").GetString());
        Assert.False(outcomes[2].GetProperty("admitted").GetBoolean());
        Assert.Equal(new[] { "unsupported", "unsupported", "missingItem", "unsupported" }, outcomes.Select(value => value.GetProperty("subclass").GetProperty("state").GetString()));
        Assert.True(store.Last!.IncludeSubclass); Assert.Equal(1, store.Prepares);
    }

    [Theory]
    [InlineData("itemIds", "[]")]
    [InlineData("itemIds", "[7]")]
    [InlineData("itemIds", "[\"01\"]")]
    [InlineData("itemIds", "[\"4294967296\"]")]
    [InlineData("itemIds", "[\"-1\"]")]
    [InlineData("includeSubclass", "null")]
    [InlineData("includeSubclass", "\"true\"")]
    [InlineData("includeSubclass", "1")]
    [InlineData("researchLocale", "\"us\"")]
    [InlineData("artifactId", "\"../index\"")]
    [InlineData("url", "\"https://example.org/source\"")]
    [InlineData("policyRevision", "null")]
    [InlineData("policyRevision", "1")]
    [InlineData("policyRevision", "\"classification-label-index-v3\"")]
    [InlineData("policyRevision", "\"\"")]
    public async Task Invalid_primitive_authority_never_reaches_store(string field, string raw)
    {
        var store = new Store(); await using var labels = new ItemClassificationsService(store);
        var parameters = JsonSerializer.SerializeToNode(Scope("7"), Json)!.AsObject(); parameters[field] = JsonNode.Parse(raw);
        var frames = await Run(labels, Request("invalid", "items.classifications", parameters));
        Assert.Equal("INVALID_REQUEST", Reply(frames, "invalid").GetProperty("error").GetProperty("code").GetString()); Assert.Equal(0, store.Prepares);
    }

    [Fact]
    public async Task Missing_service_has_no_capability_and_cannot_simulate_labels()
    {
        var frames = await Run(null, Request("hello", "hello", new { }), Request("labels", "items.classifications", Scope("7")));
        Assert.DoesNotContain(Reply(frames, "hello").GetProperty("result").GetProperty("capabilities").EnumerateArray(), value => value.GetString() == "item-classifications-v1");
        Assert.Equal("ITEM_CLASSIFICATIONS_UNAVAILABLE", Reply(frames, "labels").GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("classification-label-index-v1")]
    public async Task Old_shape_and_explicit_v1_preserve_unsupported_subclass_when_new_service_is_available(string? policy)
    {
        var store = new Store { SupportsSubclassLabels = true }; await using var labels = new ItemClassificationsService(store);
        var parameters = Scope("7"); parameters["includeSubclass"] = true; if (policy is not null) parameters["policyRevision"] = policy;
        var frames = await Run(labels, Request("hello", "hello", new { }), Request("legacy", "items.classifications", parameters));
        Assert.Contains(Reply(frames, "hello").GetProperty("result").GetProperty("capabilities").EnumerateArray(), value => value.GetString() == "item-subclass-labels-v1");
        var result = Reply(frames, "legacy").GetProperty("result");
        Assert.Equal("classification-label-index-v1", result.GetProperty("policyRevision").GetString());
        Assert.Equal("unsupported", result.GetProperty("sources")[1].GetProperty("state").GetString());
        Assert.Equal("unsupported", result.GetProperty("outcomes")[0].GetProperty("subclass").GetProperty("state").GetString());
        Assert.Equal("classification-label-index-v1", store.Last!.PolicyRevision);
    }

    [Fact]
    public async Task Class_only_service_does_not_advertise_or_accept_the_new_policy()
    {
        var store = new Store(); await using var labels = new ItemClassificationsService(store);
        var parameters = Scope("7"); parameters["policyRevision"] = "classification-label-index-v2";
        var frames = await Run(labels, Request("hello", "hello", new { }), Request("v2", "items.classifications", parameters));
        var capabilities = Reply(frames, "hello").GetProperty("result").GetProperty("capabilities").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Contains("item-classifications-v1", capabilities); Assert.DoesNotContain("item-subclass-labels-v1", capabilities);
        Assert.Equal("ITEM_SUBCLASS_UNSUPPORTED", Reply(frames, "v2").GetProperty("error").GetProperty("code").GetString()); Assert.Equal(0, store.Prepares);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_v2_echoes_actual_policy_for_class_only_and_composite_batches(bool includeSubclass)
    {
        var store = new Store { SupportsSubclassLabels = true }; await using var labels = new ItemClassificationsService(store);
        var parameters = Scope("7", "0", "7"); parameters["includeSubclass"] = includeSubclass; parameters["policyRevision"] = "classification-label-index-v2";
        var result = Reply(await Run(labels, Request("v2", "items.classifications", parameters)), "v2").GetProperty("result");
        Assert.Equal("classification-label-index-v2", result.GetProperty("policyRevision").GetString());
        Assert.Equal(includeSubclass ? 2 : 1, result.GetProperty("sources").GetArrayLength());
        var outcomes = result.GetProperty("outcomes").EnumerateArray().ToArray();
        Assert.Equal(new[] { "7", "0", "7" }, outcomes.Select(value => value.GetProperty("itemId").GetString()));
        if (includeSubclass)
        {
            Assert.Equal("95d8638f", result.GetProperty("sources")[1].GetProperty("snapshot").GetProperty("tableId").GetString());
            Assert.All(outcomes, outcome => Assert.Equal("ready", outcome.GetProperty("subclass").GetProperty("state").GetString()));
            var value = outcomes[0].GetProperty("subclass");
            Assert.Equal(new[] { "2", "0" }, value.GetProperty("sourceKeys").EnumerateArray().Select(key => key.GetProperty("value").GetString()));
            Assert.Equal("95d8638f", value.GetProperty("target").GetProperty("tableId").GetString());
        }
        else Assert.All(outcomes, outcome => Assert.Equal(JsonValueKind.Null, outcome.GetProperty("subclass").ValueKind));
        Assert.Equal("classification-label-index-v2", store.Last!.PolicyRevision); Assert.Equal(includeSubclass, store.Last.IncludeSubclass);
    }

    [Theory]
    [InlineData(ItemClassificationKind.Class)]
    [InlineData(ItemClassificationKind.Subclass)]
    public async Task V2_wire_keeps_each_kind_failure_separate_with_numeric_evidence(ItemClassificationKind failedKind)
    {
        var store = new Store { SupportsSubclassLabels = true, FailedKind = failedKind }; await using var labels = new ItemClassificationsService(store);
        var parameters = Scope("7"); parameters["includeSubclass"] = true; parameters["policyRevision"] = "classification-label-index-v2";
        var result = Reply(await Run(labels, Request("isolated", "items.classifications", parameters)), "isolated").GetProperty("result");
        var outcome = result.GetProperty("outcomes")[0];
        var failed = outcome.GetProperty(failedKind == ItemClassificationKind.Class ? "class" : "subclass");
        var ready = outcome.GetProperty(failedKind == ItemClassificationKind.Class ? "subclass" : "class");
        Assert.Equal("unavailable", failed.GetProperty("state").GetString()); Assert.Equal(JsonValueKind.Null, failed.GetProperty("target").ValueKind);
        Assert.Equal("ITEM_CLASSIFICATION_STALE", failed.GetProperty("problem").GetProperty("code").GetString());
        Assert.True(failed.GetProperty("problem").GetProperty("retryable").GetBoolean()); Assert.NotEmpty(failed.GetProperty("sourceKeys").EnumerateArray());
        Assert.Equal("ready", ready.GetProperty("state").GetString()); Assert.True(outcome.GetProperty("admitted").GetBoolean());
        Assert.Equal(new[] { failedKind == ItemClassificationKind.Class ? "unavailable" : "ready", failedKind == ItemClassificationKind.Subclass ? "unavailable" : "ready" },
            result.GetProperty("sources").EnumerateArray().Select(value => value.GetProperty("state").GetString()));
    }

    [Fact]
    public async Task Typed_failures_preserve_retryability_and_oversized_error_envelopes_are_refused()
    {
        var store = new Store { Failure = new("ITEM_CLASSIFICATION_STALE", "Authored stale admission.", true) };
        await using var labels = new ItemClassificationsService(store);
        var frame = Reply(await Run(labels, Request("stale", "items.classifications", Scope("7"))), "stale");
        Assert.Equal("ITEM_CLASSIFICATION_STALE", frame.GetProperty("error").GetProperty("code").GetString());
        Assert.True(frame.GetProperty("error").GetProperty("retryable").GetBoolean());
        store.Failure = new("AUTHORED_OVERSIZED_ERROR", new string('<', 100_000), true);
        var refused = Reply(await Run(labels, Request("limit", "items.classifications", Scope("7"))), "limit");
        Assert.Equal("ITEM_CLASSIFICATIONS_RESULT_LIMIT", refused.GetProperty("error").GetProperty("code").GetString());
        Assert.True(Encoding.UTF8.GetByteCount(refused.GetRawText()) + 1 <= ProtocolHost.MaxClassificationFrameBytes);
    }

    [Fact]
    public async Task Complete_escaped_envelope_accepts_exact_budget_and_refuses_one_byte_more_without_truncation()
    {
        var store = new Store { EmitProgress = false }; await using var labels = new ItemClassificationsService(store);
        var id = new string('<', 128); var parameters = Scope(Enumerable.Repeat("7", 50).ToArray());
        var sample = Reply(await Run(labels, Request(id, "items.classifications", parameters)), id);
        // Measure the real emitted envelope, including original source proof and escaped request ID.
        var blank = JsonNode.Parse(sample.GetRawText())!;
        foreach (var outcome in blank["result"]!["outcomes"]!.AsArray()) outcome!["class"]!["text"] = "";
        var available = ProtocolHost.MaxClassificationFrameBytes - JsonSerializer.SerializeToUtf8Bytes(blank, Json).Length - 1 - 50;
        var texts = Enumerable.Repeat("a", 50).ToArray();
        for (var i = 0; i < texts.Length && available > 0; i++)
        {
            var escaped = Math.Min(4095, available / 6); texts[i] += new string('<', escaped); available -= escaped * 6;
            var plain = Math.Min(4095 - escaped, available); texts[i] += new string('a', plain); available -= plain;
        }
        Assert.Equal(0, available); Assert.All(texts, text => Assert.InRange(text.Length, 1, 4096));
        store.Texts = texts;
        var exact = Reply(await Run(labels, Request(id, "items.classifications", parameters)), id);
        Assert.Equal(ProtocolHost.MaxClassificationFrameBytes, Encoding.UTF8.GetByteCount(exact.GetRawText()) + 1);
        Assert.Equal(50, exact.GetProperty("result").GetProperty("outcomes").GetArrayLength());
        Assert.Equal(texts[0], exact.GetProperty("result").GetProperty("outcomes")[0].GetProperty("class").GetProperty("text").GetString());
        var index = Array.FindIndex(texts, text => text.Length < 4096); Assert.True(index >= 0); texts[index] += "a";
        var refused = Reply(await Run(labels, Request(id, "items.classifications", parameters)), id);
        Assert.Equal("ITEM_CLASSIFICATIONS_RESULT_LIMIT", refused.GetProperty("error").GetProperty("code").GetString());
        Assert.False(refused.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task Two_kind_envelope_counts_both_source_proofs_and_labels_at_exact_limit()
    {
        var store = new Store { SupportsSubclassLabels = true, EmitProgress = false }; await using var labels = new ItemClassificationsService(store);
        var id = new string('<', 128); var parameters = Scope(Enumerable.Repeat("7", 50).ToArray());
        parameters["includeSubclass"] = true; parameters["policyRevision"] = "classification-label-index-v2";
        var sample = Reply(await Run(labels, Request(id, "items.classifications", parameters)), id);
        var blank = JsonNode.Parse(sample.GetRawText())!;
        foreach (var outcome in blank["result"]!["outcomes"]!.AsArray()) { outcome!["class"]!["text"] = ""; outcome["subclass"]!["text"] = ""; }
        var available = ProtocolHost.MaxClassificationFrameBytes - JsonSerializer.SerializeToUtf8Bytes(blank, Json).Length - 1 - 100;
        var texts = Enumerable.Repeat("a", 100).ToArray();
        for (var i = 0; i < texts.Length && available > 0; i++)
        {
            var escaped = Math.Min(4095, available / 6); texts[i] += new string('<', escaped); available -= escaped * 6;
            var plain = Math.Min(4095 - escaped, available); texts[i] += new string('a', plain); available -= plain;
        }
        Assert.Equal(0, available); Assert.All(texts, text => Assert.InRange(text.Length, 1, 4096));
        store.Texts = texts[..50]; store.SubclassTexts = texts[50..];
        var exact = Reply(await Run(labels, Request(id, "items.classifications", parameters)), id);
        Assert.Equal(ProtocolHost.MaxClassificationFrameBytes, Encoding.UTF8.GetByteCount(exact.GetRawText()) + 1);
        Assert.Equal(2, exact.GetProperty("result").GetProperty("sources").GetArrayLength());
        Assert.Equal(50, exact.GetProperty("result").GetProperty("outcomes").GetArrayLength());
        Assert.Equal(texts[50], exact.GetProperty("result").GetProperty("outcomes")[0].GetProperty("subclass").GetProperty("text").GetString());
        var index = Array.FindIndex(store.SubclassTexts, text => text.Length < 4096); Assert.True(index >= 0); store.SubclassTexts[index] += "a";
        var refused = Reply(await Run(labels, Request(id, "items.classifications", parameters)), id);
        Assert.Equal("ITEM_CLASSIFICATIONS_RESULT_LIMIT", refused.GetProperty("error").GetProperty("code").GetString()); Assert.False(refused.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task Oversized_two_kind_encoding_stops_before_traversing_the_full_rejected_result()
    {
        var text = new string('<', 4096);
        var store = new Store { SupportsSubclassLabels = true, EmitProgress = false, CountOutcomes = true,
            Texts = Enumerable.Repeat(text, 50).ToArray(), SubclassTexts = Enumerable.Repeat(text, 50).ToArray() };
        await using var labels = new ItemClassificationsService(store);
        var parameters = Scope(Enumerable.Repeat("7", 50).ToArray()); parameters["includeSubclass"] = true; parameters["policyRevision"] = "classification-label-index-v2";
        var frames = await Run(labels, Request(new string('<', 128), "items.classifications", parameters));
        var refusal = Assert.Single(frames);
        Assert.Equal("ITEM_CLASSIFICATIONS_RESULT_LIMIT", refusal.GetProperty("error").GetProperty("code").GetString());
        Assert.False(refusal.TryGetProperty("result", out _));
        // The serializer must stop at the finite sink, without expanding the remaining
        // fifty pairs into the >2.4MiB rejected JSON or publishing a partial frame.
        Assert.InRange(store.EnumeratedOutcomes, 1, 49);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Blocked_output_flush_bounds_completed_payloads_and_encoding_until_actual_retirement(bool shutdown)
    {
        var previous = SynchronizationContext.Current; var context = new QueuedContext();
        // Normal delivery holds two completed batches; shutdown instead holds the
        // second producer until cancellation is actually observed and cleanup retires.
        var store = new Store { SupportsSubclassLabels = true, EmitProgress = false, CountOutcomes = true, HoldSecond = shutdown };
        var first = Scope("7"); first["policyRevision"] = "classification-label-index-v2";
        var second = Scope("8"); second["policyRevision"] = "classification-label-index-v2";
        var third = Scope("9"); third["policyRevision"] = "classification-label-index-v2";
        var requests = new[] { Request("one", "items.classifications", first), Request("two", "items.classifications", second), Request("three", "items.classifications", third) };
        using var input = new HeldInput(Encoding.UTF8.GetBytes(string.Join('\n', requests.Select(value => JsonSerializer.Serialize(value, Json))) + "\n"));
        using var output = new HeldFlush();
        using var stopping = new CancellationTokenSource();
        SynchronizationContext.SetSynchronizationContext(context);
        var labels = new ItemClassificationsService(store); Task? running = null, labelDisposal = null;
        try
        {
            running = new ProtocolHost(new DiscoveryService(new Unused(), new Unused()), itemClassifications: labels).RunAsync(input, output, stopping.Token);
            // Wait for actual transport/producer events: a semaphore wake need not
            // have posted its continuation when the current queue first becomes empty.
            SynchronizationContext.SetSynchronizationContext(previous);
            await context.DrainUntilAsync(() => output.Started.Task.IsCompleted && store.SecondPrepared.Task.IsCompleted && input.Waiting.Task.IsCompleted);
            SynchronizationContext.SetSynchronizationContext(context); context.Drain();
            Assert.True(output.Started.Task.IsCompleted); Assert.Equal(2, store.Prepares);
            Assert.Equal(1, store.EnumeratedOutcomes); Assert.False(running.IsCompleted);
            if (shutdown)
            {
                stopping.Cancel();
                SynchronizationContext.SetSynchronizationContext(previous);
                await context.DrainUntilAsync(() => store.SecondStopping.Task.IsCompleted);
                SynchronizationContext.SetSynchronizationContext(context); context.Drain();
                Assert.False(running.IsCompleted); Assert.Equal(2, store.Prepares); Assert.Equal(1, store.EnumeratedOutcomes);
                store.SecondCleanup.TrySetResult();
            }
            output.Release.TrySetResult(); context.Drain();
            SynchronizationContext.SetSynchronizationContext(previous);
            await context.DrainUntilAsync(() => store.Prepares == (shutdown ? 2 : 3) && store.EnumeratedOutcomes == (shutdown ? 1 : 3) && output.Flushes == 3);
            SynchronizationContext.SetSynchronizationContext(context);
            Assert.Equal(shutdown ? 2 : 3, store.Prepares); Assert.Equal(shutdown ? 1 : 3, store.EnumeratedOutcomes); Assert.Equal(3, output.Flushes);
            input.Release.TrySetResult(); context.Drain();
            SynchronizationContext.SetSynchronizationContext(previous);
            await context.DrainUntilAsync(() => running.IsCompleted);
            Assert.True(running.IsCompletedSuccessfully);
        }
        finally
        {
            store.SecondCleanup.TrySetResult(); output.Release.TrySetResult(); input.Release.TrySetResult(); context.Drain();
            SynchronizationContext.SetSynchronizationContext(previous);
            await context.DrainUntilAsync(() => running?.IsCompleted ?? true);
            SynchronizationContext.SetSynchronizationContext(context);
            labelDisposal = labels.DisposeAsync().AsTask(); context.Drain();
            SynchronizationContext.SetSynchronizationContext(previous);
            await context.DrainUntilAsync(() => labelDisposal.IsCompleted);
        }
        await running!.WaitAsync(TimeSpan.FromSeconds(5)); await labelDisposal.WaitAsync(TimeSpan.FromSeconds(5));
        var replies = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }).ToArray();
        Assert.Equal(new[] { "one", "three", "two" }, replies.Select(value => value.GetProperty("id").GetString()).Order());
        Assert.True(Reply(replies, "one").TryGetProperty("result", out _));
        if (shutdown)
        {
            Assert.Equal("CANCELED", Reply(replies, "two").GetProperty("error").GetProperty("code").GetString());
            Assert.Equal("CANCELED", Reply(replies, "three").GetProperty("error").GetProperty("code").GetString());
        }
        else
        {
            Assert.True(Reply(replies, "two").TryGetProperty("result", out _));
            Assert.True(Reply(replies, "three").TryGetProperty("result", out _));
        }
    }

    [Theory]
    [InlineData("classification-label-index-v1")]
    [InlineData("classification-label-index-v2")]
    public async Task Shared_requests_cancel_independently_and_last_terminal_waits_for_actual_cleanup(string policy)
    {
        var store = new GatedStore(); await using var labels = new ItemClassificationsService(store);
        await using var session = await Session.Start(labels);
        try
        {
            var parameters = Scope("7"); parameters["policyRevision"] = policy; parameters["includeSubclass"] = true;
            await session.Send("one", "items.classifications", parameters); await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await session.Send("two", "items.classifications", parameters); await session.Send("barrier", "hello", new { });
            Assert.Equal("barrier", (await session.Receive()).GetProperty("id").GetString());
            await session.Send("cancel-one", "request.cancel", new { requestId = "one" });
            var first = new[] { await session.Receive(), await session.Receive() };
            Assert.Equal("CANCELED", Reply(first, "one").GetProperty("error").GetProperty("code").GetString());
            Assert.True(Reply(first, "cancel-one").GetProperty("result").GetProperty("requested").GetBoolean()); Assert.False(store.Token.IsCancellationRequested);
            await session.Send("cancel-two", "request.cancel", new { requestId = "two" });
            Assert.Equal("cancel-two", (await session.Receive()).GetProperty("id").GetString());
            await store.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await session.Send("during-cleanup", "hello", new { });
            Assert.Equal("during-cleanup", (await session.Receive()).GetProperty("id").GetString());
            store.Cleanup.TrySetResult();
            var terminal = await session.Receive(); Assert.Equal("two", terminal.GetProperty("id").GetString());
            Assert.Equal("CANCELED", terminal.GetProperty("error").GetProperty("code").GetString()); Assert.Equal(1, store.Prepares);
        }
        finally { store.Cleanup.TrySetResult(); }
    }

    [Fact]
    public async Task Same_ids_with_different_policies_remain_separate_producers()
    {
        var store = new GatedStore(); await using var labels = new ItemClassificationsService(store); await using var session = await Session.Start(labels);
        try
        {
            var legacy = Scope("7"); legacy["includeSubclass"] = true;
            var composite = Scope("7"); composite["includeSubclass"] = true; composite["policyRevision"] = "classification-label-index-v2";
            await session.Send("v1", "items.classifications", legacy); await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await session.Send("v2", "items.classifications", composite); await session.Send("barrier", "hello", new { }); await session.Receive();
            await session.Send("cancel-v1", "request.cancel", new { requestId = "v1" });
            Assert.Equal("cancel-v1", (await session.Receive()).GetProperty("id").GetString());
            await store.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(1, store.Prepares);
            store.Cleanup.TrySetResult(); var first = await session.Receive();
            Assert.Equal("v1", first.GetProperty("id").GetString()); Assert.Equal("CANCELED", first.GetProperty("error").GetProperty("code").GetString());
            await store.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(2, store.Prepares);
            Assert.Equal(new[] { "classification-label-index-v1", "classification-label-index-v2" }, store.Policies);
            await session.Send("cancel-v2", "request.cancel", new { requestId = "v2" });
            var second = new[] { await session.Receive(), await session.Receive() };
            Assert.Equal("CANCELED", Reply(second, "v2").GetProperty("error").GetProperty("code").GetString());
        }
        finally { store.Cleanup.TrySetResult(); }
    }

    [Fact]
    public async Task Canceling_a_Host_publication_waiter_preserves_both_active_shared_consumers()
    {
        var store = new GatedStore(); await using var labels = new ItemClassificationsService(store); await using var session = await Session.Start(labels);
        try
        {
            var parameters = Scope("7"); parameters["policyRevision"] = "classification-label-index-v2";
            var queued = Scope("8"); queued["policyRevision"] = "classification-label-index-v2";
            await session.Send("one", "items.classifications", parameters); await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await session.Send("two", "items.classifications", parameters); await session.Send("queued", "items.classifications", queued);
            await session.Send("barrier", "hello", new { }); Assert.Equal("barrier", (await session.Receive()).GetProperty("id").GetString());
            await session.Send("cancel-queued", "request.cancel", new { requestId = "queued" });
            var canceled = new[] { await session.Receive(), await session.Receive() };
            Assert.True(Reply(canceled, "cancel-queued").GetProperty("result").GetProperty("requested").GetBoolean());
            Assert.Equal("CANCELED", Reply(canceled, "queued").GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(1, store.Prepares); Assert.False(store.Token.IsCancellationRequested);
            await session.Send("cancel-one", "request.cancel", new { requestId = "one" });
            var first = new[] { await session.Receive(), await session.Receive() };
            Assert.Equal("CANCELED", Reply(first, "one").GetProperty("error").GetProperty("code").GetString());
            Assert.False(store.Token.IsCancellationRequested); Assert.Equal(1, store.Prepares);
            await session.Send("cancel-two", "request.cancel", new { requestId = "two" }); await session.Receive();
            await store.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(5)); store.Cleanup.TrySetResult();
            Assert.Equal("CANCELED", (await session.Receive()).GetProperty("error").GetProperty("code").GetString());
        }
        finally { store.Cleanup.TrySetResult(); }
    }

    [Fact]
    public async Task Shutdown_acknowledgement_does_not_finish_the_session_before_v2_cleanup()
    {
        var store = new GatedStore(); await using var labels = new ItemClassificationsService(store); await using var session = await Session.Start(labels);
        try
        {
            var parameters = Scope("7"); parameters["includeSubclass"] = true; parameters["policyRevision"] = "classification-label-index-v2";
            await session.Send("active", "items.classifications", parameters); await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await session.Send("shutdown", "shutdown", new { });
            Assert.True((await session.Receive()).GetProperty("result").GetProperty("stopping").GetBoolean());
            await store.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(session.Completion.IsCompleted);
            store.Cleanup.TrySetResult(); var terminal = await session.Receive();
            Assert.Equal("active", terminal.GetProperty("id").GetString()); Assert.Equal("CANCELED", terminal.GetProperty("error").GetProperty("code").GetString());
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { store.Cleanup.TrySetResult(); }
    }

    private static JsonElement Reply(IEnumerable<JsonElement> frames, string id) => Assert.Single(frames, frame => frame.TryGetProperty("id", out var value) && value.GetString() == id);
    private static async Task<JsonElement[]> Run(ItemClassificationsService? labels, params object[] requests)
    {
        // Keep input open until the requested terminals arrive; EOF legitimately cancels active work.
        await using var session = await Session.Start(labels);
        foreach (var request in requests) await session.SendRaw(JsonSerializer.Serialize(request, Json));
        var frames = new List<JsonElement>(); var replies = 0;
        while (replies < requests.Length) { var frame = await session.Receive(); frames.Add(frame); if (frame.TryGetProperty("id", out _)) replies++; }
        return frames.ToArray();
    }
    private sealed class Store : IItemClassificationsStore
    {
        public bool SupportsSubclassLabels { get; init; }
        internal int Prepares, EnumeratedOutcomes;
        internal ItemClassificationBatchRequest? Last;
        internal bool EmitProgress = true, CountOutcomes;
        internal bool HoldSecond;
        internal readonly TaskCompletionSource SecondPrepared = new(TaskCreationOptions.RunContinuationsAsynchronously),
            SecondStopping = new(TaskCreationOptions.RunContinuationsAsynchronously), SecondCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string[]? Texts, SubclassTexts;
        internal DiscoveryException? Failure;
        internal ItemClassificationKind? FailedKind;
        public async Task<ItemClassificationBatch> PrepareBatchAsync(ItemClassificationBatchRequest request, Func<ItemClassificationProgress, Task> progress, CancellationToken token)
        {
            Prepares++; Last = request; if (Failure is not null) throw Failure;
            if (EmitProgress) await progress(new("Looking up authored class labels", "rows", request.ItemIds.Count, request.ItemIds.Count, "b977271e"));
            var source = Source();
            var v2 = request.PolicyRevision == "classification-label-index-v2";
            var stale = new ItemClassificationProblem("ITEM_CLASSIFICATION_STALE", "Authored optional target replacement.", true);
            var sources = new List<ItemClassificationSource> { FailedKind == ItemClassificationKind.Class
                ? new(ItemClassificationKind.Class, ItemClassificationState.Unavailable, null, null, stale)
                : new(ItemClassificationKind.Class, ItemClassificationState.Ready, source, new string('c', 64)) };
            if (request.IncludeSubclass) sources.Add(!v2
                ? new(ItemClassificationKind.Subclass, ItemClassificationState.Unsupported, null, null, new("ITEM_SUBCLASS_UNSUPPORTED", "Subclass labels are not qualified."))
                : FailedKind == ItemClassificationKind.Subclass
                    ? new(ItemClassificationKind.Subclass, ItemClassificationState.Unavailable, null, null, stale)
                    : new(ItemClassificationKind.Subclass, ItemClassificationState.Ready, Source(true), new string('d', 64)));
            var outcomes = request.ItemIds.Select((id, index) =>
            {
                var keys = new ItemSourceValue[] { new("50238ec2", 1, "2") };
                var subclassKeys = new ItemSourceValue[] { keys[0], new("50238ec2", 2, "0") };
                var value = id == "4294967295" ? new ItemClassificationValue(ItemClassificationState.MissingItem, [])
                    : FailedKind == ItemClassificationKind.Class ? new(ItemClassificationState.Unavailable, keys, Problem: stale)
                    : id == "0" ? new(ItemClassificationState.EmptyText, keys, "", new("b977271e", "7", 2, false, null), "1")
                    : new(ItemClassificationState.Ready, keys, Texts?[index] ?? " Authored <label> ", new("b977271e", "8", 2, true, "3"), "1");
                return new ItemClassificationOutcome(id, id != "4294967295", value,
                    request.IncludeSubclass ? id == "4294967295" ? new(ItemClassificationState.MissingItem, [])
                        : !v2 ? new(ItemClassificationState.Unsupported, subclassKeys, Problem: new("ITEM_SUBCLASS_UNSUPPORTED", "Subclass labels are not qualified."))
                        : FailedKind == ItemClassificationKind.Subclass ? new(ItemClassificationState.Unavailable, subclassKeys, Problem: stale)
                        : new(ItemClassificationState.Ready, subclassKeys, SubclassTexts?[index] ?? "Authored subclass", new("95d8638f", "19", 2, false, null), "1") : null);
            }).ToArray();
            var batch = new ItemClassificationBatch(request.Context, request.ArtifactId, request.PolicyRevision, sources,
                CountOutcomes ? new CountedOutcomes(outcomes, () => EnumeratedOutcomes++) : outcomes);
            if (Prepares == 2)
            {
                SecondPrepared.TrySetResult();
                if (HoldSecond)
                {
                    try { await Task.Delay(Timeout.Infinite, token); }
                    finally { SecondStopping.TrySetResult(); await SecondCleanup.Task; }
                    token.ThrowIfCancellationRequested();
                }
            }
            return batch;
        }
    }
    private static TableDataSnapshot Source(bool subclass = false)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "contracts/discovery.schema.json"))) directory = directory.Parent;
        if (directory is null) throw new InvalidOperationException("Repository authored fixture is required.");
        var snapshot = JsonSerializer.Deserialize<ItemsSnapshot>(File.ReadAllText(Path.Combine(directory.FullName, "tests/desktop/fixtures/items-snapshot.json")), Json)!;
        return snapshot.ItemSource with { TableId = subclass ? "95d8638f" : "b977271e", Association = snapshot.ItemSource.Association with { FileDataId = subclass ? "1261604" : "1140189" },
            Columns = subclass ? [new("ClassID", "integer", false, null), new("SubClassID", "integer", false, null), new("DisplayName_lang", "string", false, null)]
                : [new("ID", "integer", true, null), new("ClassID", "integer", false, null), new("ClassName_lang", "string", false, null)] };
    }
    private sealed class CountedOutcomes(IReadOnlyList<ItemClassificationOutcome> values, Action visit) : IReadOnlyList<ItemClassificationOutcome>
    {
        public int Count => values.Count;
        public ItemClassificationOutcome this[int index] => values[index];
        public IEnumerator<ItemClassificationOutcome> GetEnumerator() { foreach (var value in values) { visit(); yield return value; } }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class HeldInput(byte[] bytes) : MemoryStream(bytes)
    {
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length) return await base.ReadAsync(buffer, cancellationToken);
            Waiting.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); return 0;
        }
    }
    private sealed class HeldFlush : MemoryStream
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Flushes;
        public override async Task FlushAsync(CancellationToken cancellationToken)
        { if (++Flushes == 1) { Started.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); } }
    }
    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> pending = new();
        private readonly SemaphoreSlim posted = new(0, 1);
        public override void Post(SendOrPostCallback callback, object? state)
        { lock (pending) { pending.Enqueue((callback, state)); if (posted.CurrentCount == 0) posted.Release(); } }
        internal async Task DrainUntilAsync(Func<bool> completed)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!completed())
            {
                Drain();
                if (!completed()) await posted.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
        }
        internal void Drain()
        {
            while (true)
            {
                (SendOrPostCallback Callback, object? State) work;
                lock (pending) { if (!pending.TryDequeue(out work)) return; }
                var previous = Current; SetSynchronizationContext(this);
                try { work.Callback(work.State); } finally { SetSynchronizationContext(previous); }
            }
        }
    }
    private sealed class GatedStore : IItemClassificationsStore
    {
        public bool SupportsSubclassLabels => true;
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously), Stopping = new(TaskCreationOptions.RunContinuationsAsynchronously), Cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource SecondStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<string> Policies = [];
        internal CancellationToken Token; internal int Prepares;
        public async Task<ItemClassificationBatch> PrepareBatchAsync(ItemClassificationBatchRequest request, Func<ItemClassificationProgress, Task> progress, CancellationToken token)
        {
            Prepares++; Policies.Add(request.PolicyRevision); Token = token; Started.TrySetResult(); if (Prepares == 2) SecondStarted.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { Stopping.TrySetResult(); await Cleanup.Task; }
            throw new InvalidOperationException("Authored producer retires only on cancellation.");
        }
    }
    private sealed class Session : IAsyncDisposable
    {
        private readonly TcpClient client, server; private readonly StreamWriter writer; private readonly StreamReader reader; private readonly Task completion;
        internal Task Completion => completion;
        private Session(TcpClient client, TcpClient server, ItemClassificationsService? labels)
        {
            this.client = client; this.server = server; writer = new(client.GetStream(), new UTF8Encoding(false), leaveOpen: true); reader = new(client.GetStream(), Encoding.UTF8, leaveOpen: true);
            completion = new ProtocolHost(new DiscoveryService(new Unused(), new Unused()), itemClassifications: labels).RunAsync(server.GetStream(), server.GetStream());
        }
        internal static async Task<Session> Start(ItemClassificationsService? labels)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var client = new TcpClient();
            await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint); var server = await listener.AcceptTcpClientAsync(); return new(client, server, labels);
        }
        internal Task Send(string id, string method, object parameters) => SendRaw(JsonSerializer.Serialize(Request(id, method, parameters), Json));
        internal async Task SendRaw(string frame) { await writer.WriteLineAsync(frame); await writer.FlushAsync(); }
        internal async Task<JsonElement> Receive() { using var document = JsonDocument.Parse((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!); return document.RootElement.Clone(); }
        public async ValueTask DisposeAsync() { client.Dispose(); try { await completion.WaitAsync(TimeSpan.FromSeconds(10)); } finally { server.Dispose(); reader.Dispose(); await writer.DisposeAsync(); } }
    }
    private sealed class Unused : IDiscoverySource, IDiscoveryCatalog
    {
        public Task<DiscoveryAttempt> FetchAsync(string product, CancellationToken token) => throw new InvalidOperationException();
        public Task<long> BeginRefreshAsync(string product, CancellationToken token) => throw new InvalidOperationException();
        public Task<DiscoveryPage> CommitAsync(string product, long generation, DiscoveryAttempt attempt, string completedAt, CancellationToken token) => throw new InvalidOperationException();
        public Task<DiscoveryPage> ReadAsync(string product, string filter, int limit, string? cursor, CancellationToken token) => throw new InvalidOperationException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
