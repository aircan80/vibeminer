using System.Globalization;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Protocols.Tact;

namespace VibeMiner.Infrastructure;

/// <summary>Physical encoded acquisition only. No decoded identity, schema or feature readiness is inferred.</summary>
public sealed class OfficialEncodedObjectProducer(DiscoveryCatalog catalog, VerifiedObjectStore objects,
    HttpClient client, AcquisitionResources resources, EncodedSourcePlans? sourcePlans = null) : IEncodedObjectProducer
{
    public const string VerifierRevision = "blte-encoded-v1";
    private readonly EncodedSourcePlans plans = sourcePlans ?? new(catalog, objects, resources);

    public async Task<EncodedReceipt> ProduceAsync(EncodedRequirement requirement, IAcquisitionWorkContext context,
        CancellationToken token)
    {
        if (requirement.VerifierRevision != VerifierRevision || requirement.EncodingKey.Length != 32 ||
            requirement.EncodingKey.Any(ch => ch is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            requirement.EncodingKey.All(ch => ch == '0') || requirement.EncodedBytes is < 9 or > VerifiedObjectStore.MaxObjectBytes)
            throw Error("ENCODED_REQUIREMENT_UNSUPPORTED", "A canonical supported exact encoded identity and length are required.");
        // Source authority is checked even for local reuse: sharing bytes does not admit arbitrary source plans.
        var wire = new WireProgress(context);
        var attempted = new HashSet<EncodedSourceContext>();
        DiscoveryException? last = null;
        while (true)
        {
            var sources = context.Snapshot.Sources.Where(source => !attempted.Contains(source)).ToArray();
            if (sources.Length == 0) throw last ?? Error("ENCODED_SOURCE_MISSING", "No retained exact source context authorized this object.");
            foreach (var source in sources)
            {
                token.ThrowIfCancellationRequested();
                attempted.Add(source);
                EncodedSourcePlanLease lease;
                try { lease = await plans.AcquireAsync(source, token, context); }
                catch (DiscoveryException failure)
                {
                    // A failed authority belongs only to this plan, never to the shared EKey.
                    // Cancellation propagates; a later valid source must establish its own authority.
                    last = failure; continue;
                }
                await using (lease)
                {
                var retained = await catalog.FindEncodedReceiptAsync(requirement.EncodingKey, requirement.EncodedBytes, token);
                if (retained is not null)
                {
                    try
                    {
                        await context.ReportAsync(new("Verifying retained encoded object", "bytes", 0, requirement.EncodedBytes));
                        var verifiedThrough = await catalog.CaptureObjectIntegrityEpochAsync(token);
                        await using (var input = await objects.OpenVerifiedAsync(retained.Sha256, requirement.EncodedBytes, resources, context, token))
                        using (await resources.EnterCpuAsync(context, token))
                        {
                            await BlteEncodedVerifier.VerifyAsync(input, requirement.EncodingKey, requirement.EncodedBytes, token);
                        }
                        var verified = retained with { VerifiedAt = DateTimeOffset.UtcNow.ToString("O"), VerifierRevision = VerifierRevision };
                        await catalog.RecordEncodedReceiptAsync(verified, token);
                        await catalog.ClearKnownCorruptAsync(verified.EncodingKey, verified.EncodedBytes, verified.Sha256, verifiedThrough, token);
                        return verified;
                    }
                    catch (DiscoveryException failure) when (failure.Code is "LOCAL_OBJECT_CORRUPT" or "BLTE_INTEGRITY" or "BLTE_INVALID")
                    {
                        await catalog.RecordEncodedInvalidityAsync(retained.EncodingKey, retained.EncodedBytes, retained.Sha256, failure.Code, token);
                        if (failure.Code != "LOCAL_OBJECT_CORRUPT") throw;
                        await context.ReportAsync(new("Repairing unavailable retained encoded object", "bytes", 0, requirement.EncodedBytes));
                    }
                    catch (DiscoveryException failure) when (failure.Code == "LOCAL_OBJECT_MISSING")
                    { await context.ReportAsync(new("Repairing unavailable retained encoded object", "bytes", 0, requirement.EncodedBytes)); }
                }
                try { return await TransferAsync(requirement, lease, context, wire, token); }
                catch (DiscoveryException failure) when (Retry(failure)) { last = failure; }
                }
            }
        }
    }

    private async Task<EncodedReceipt> TransferAsync(EncodedRequirement expected, EncodedSourcePlanLease lease,
        IAcquisitionWorkContext context, WireProgress wire, CancellationToken token)
    {
        var plan = lease.Plan;
        var mapped = await plans.LookupMapAsync(lease, expected.EncodingKey, expected.EncodedBytes, token);
        if (mapped is not null && (!mapped.IsMember && mapped.Locations.Count != 0 ||
            mapped.Locations.Any(location => location.EncodedBytes != expected.EncodedBytes ||
                !plan.ArchiveKeys.Contains(location.ArchiveKey, StringComparer.Ordinal))))
            throw Error("CONTENT_LOCATOR_INTEGRITY", "A registered map supplied a location outside its exact source or ledger scope.");
        var verifiedThrough = await catalog.CaptureObjectIntegrityEpochAsync(token);
        using var staging = await resources.ReserveStagingAsync(expected.EncodedBytes, context, token);
        string? path = null;
        try
        {
            var transport = new OfficialSourceTransport(client, resources, context);
            var archives = new ArchivedContentLocator(catalog, objects, transport, plans.ArchiveCache, plan.Source.ToString(), resources);
            Uri? url = null; ContentLocation? location = null;
            await using (var encoded = await objects.CreateStagingFileAsync(token))
            {
                path = encoded.Name;
                for (var index = 0; index < plan.Routes.Count; index++)
                {
                    var route = plan.Routes[index]; url = ArchivedContentLocator.Url(route, expected.EncodingKey);
                    encoded.SetLength(0); encoded.Position = 0;
                    Task Progress(string stage) => context.ReportAsync(new(stage, "bytes"));
                    try
                    {
                        if (mapped is { IsMember: true })
                        {
                            foreach (var candidate in mapped.Locations)
                            {
                                encoded.SetLength(0); encoded.Position = 0;
                                try
                                {
                                    url = ArchivedContentLocator.Url(route, candidate.ArchiveKey);
                                    var sourceBytes = await DownloadSpansAsync(transport, url, encoded, null, candidate.Offset,
                                        candidate.EncodedBytes, context, wire, token);
                                    location = new("archive", N(candidate.Offset), N(sourceBytes), candidate.ArchiveKey);
                                    break;
                                }
                                catch (DiscoveryException failure) when (Retry(failure)) { }
                            }
                            if (location is not null) break;
                            encoded.SetLength(0); encoded.Position = 0;
                            url = ArchivedContentLocator.Url(route, expected.EncodingKey);
                        }
                        try
                        {
                            if (expected.EncodedBytes <= 4L << 20)
                                await transport.DownloadLooseAsync(url, encoded, expected.EncodedBytes, "encoded object", Progress, token, transferred: wire.AddAsync);
                            else
                                await DownloadSpansAsync(transport, url, encoded, expected.EncodedBytes, 0,
                                    expected.EncodedBytes, context, wire, token);
                            location = new("loose", "0", N(expected.EncodedBytes), null);
                        }
                        catch (DiscoveryException failure) when (failure.Code is "CONTENT_LOOSE_MISSING" or "CONTENT_SOURCE_MISSING")
                        {
                            encoded.SetLength(0); encoded.Position = 0;
                            if (mapped is { IsMember: true })
                                throw Error("CONTENT_NOT_LOCATED", "This exact DL member was not supplied by its sealed archive map or loose route.", true);
                            var located = await archives.LocateAsync(route, plan.Cdn, plan.Sha256, expected.EncodingKey,
                                expected.EncodedBytes, Progress, token);
                            url = ArchivedContentLocator.Url(route, located.ArchiveKey);
                            var sourceBytes = await DownloadSpansAsync(transport, url, encoded, null, located.Offset,
                                located.EncodedBytes, context, wire, token);
                            location = new("archive", N(located.Offset), N(sourceBytes), located.ArchiveKey);
                        }
                        break;
                    }
                    catch (DiscoveryException failure) when (index + 1 < plan.Routes.Count && Retry(failure))
                    { await context.ReportAsync(new("Trying another advertised official CDN route", "bytes")); }
                }
                if (location is null) throw Error("CONTENT_UNAVAILABLE", "No admitted official source supplied this encoded object.", true);
                await encoded.FlushAsync(token); encoded.Position = 0;
                await wire.SettleAsync();
                await context.ReportAsync(new("Verifying encoded object", "bytes", 0, expected.EncodedBytes));
                using (await resources.EnterCpuAsync(context, token))
                    await BlteEncodedVerifier.VerifyAsync(encoded, expected.EncodingKey, expected.EncodedBytes, token);
            }
            var sha = await objects.PublishEncodedFileAsync(path, expected.EncodedBytes, expected.EncodingKey, resources, context, token);
            path = null;
            var now = DateTimeOffset.UtcNow.ToString("O");
            var receipt = new EncodedReceipt(expected.EncodingKey, sha, expected.EncodedBytes,
                url!.AbsoluteUri, now, now, VerifierRevision, location!);
            // CAS publication precedes SQL. A failed commit gives no durable receipt credit.
            await catalog.RecordEncodedReceiptAsync(receipt, token);
            await catalog.ClearKnownCorruptAsync(receipt.EncodingKey, receipt.EncodedBytes, receipt.Sha256, verifiedThrough, token);
            if ((await catalog.ReadKnownCorruptAsync([(receipt.EncodingKey, receipt.EncodedBytes)], token)).Contains(receipt.EncodingKey))
            {
                // Repair may itself observe the old corrupt destination after the transfer's
                // fence. Only a fresh complete canonical proof may clear that newer observation.
                var repairVerifiedThrough = await catalog.CaptureObjectIntegrityEpochAsync(token);
                try
                {
                    await using (var current = await objects.OpenVerifiedAsync(receipt.Sha256, receipt.EncodedBytes, resources, context, token))
                    using (await resources.EnterCpuAsync(context, token))
                    {
                        await BlteEncodedVerifier.VerifyAsync(current, receipt.EncodingKey, receipt.EncodedBytes, token);
                    }
                }
                catch (DiscoveryException failure) when (failure.Code is "LOCAL_OBJECT_CORRUPT" or "BLTE_INTEGRITY" or "BLTE_INVALID")
                {
                    await catalog.RecordEncodedInvalidityAsync(receipt.EncodingKey, receipt.EncodedBytes, receipt.Sha256, failure.Code, token);
                    throw;
                }
                await catalog.ClearKnownCorruptAsync(receipt.EncodingKey, receipt.EncodedBytes, receipt.Sha256, repairVerifiedThrough, token);
            }
            await context.ReportAsync(new("Encoded object retained", "bytes", expected.EncodedBytes, expected.EncodedBytes));
            return receipt;
        }
        finally
        {
            if (path is not null)
            {
                if (File.Exists(path)) File.Delete(path);
                await wire.SettleAsync();
            }
        }
    }
    private static async Task<long> DownloadSpansAsync(OfficialSourceTransport transport, Uri url,
        Stream destination, long? sourceBytes, long offset, long encodedBytes,
        IAcquisitionWorkContext context, WireProgress wire, CancellationToken token)
    {
        long done = 0;
        while (done < encodedBytes)
        {
            var length = Math.Min(4L << 20, encodedBytes - done);
            // Release and readmit the HTTP permit at every authenticated final-object span.
            // Demotion never restarts bytes already staged; no partial object is published.
            sourceBytes = await transport.DownloadRangeAsync(url, destination, sourceBytes, checked(offset + done),
                length, _ => Task.CompletedTask, token, wire.AddAsync);
            done += length;
            await context.ReportAsync(new("Downloading encoded object", "bytes", done, encodedBytes));
        }
        return sourceBytes!.Value;
    }

    private sealed class WireProgress(IAcquisitionWorkContext context)
    {
        private long bytes;
        internal Task AddAsync(long count)
        {
            bytes = checked(bytes + count);
            return context.ReportAsync(new("Downloading encoded object", "wireBytes", bytes));
        }
        internal Task SettleAsync() => context.ReportAsync(new("Encoded payload transfer finished", "wireBytes", bytes));
    }

    private static bool Retry(DiscoveryException failure) => failure.Code is "METADATA_UNAVAILABLE" or
        "CONTENT_UNAVAILABLE" or "NETWORK_UNAVAILABLE" or "NETWORK_IO" or "CONTENT_TIMEOUT" or
        "CONTENT_SOURCE_MISSING" or "CONTENT_LOCATOR_INCOMPLETE" or "CONTENT_NOT_LOCATED" or "CONTENT_LOCATOR_UNSUPPORTED";
    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static DiscoveryException Error(string code, string message, bool retryable = false) => new(code, message, retryable);
}
