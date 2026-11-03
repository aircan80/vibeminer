using VibeMiner.Cdn;
using VibeMiner.Objects;

namespace VibeMiner.ObjectAccess.Testing;

public sealed partial class InMemoryLibraryObjectAccess
{
    public sealed record ArchiveReceipt(CdnKey Archive, long Offset, long Length, CdnContext Context);
    private sealed class ArchiveGroup(Claim claim, PlannedRange range, CdnContext context, StreamScheduler.Ticket ticket)
    {
        public long Transferred;
        public readonly Claim Claim = claim;
        public readonly PlannedRange Range = range;
        public readonly CdnContext Context = context;
        public readonly StreamScheduler.Ticket Ticket = ticket;
        public readonly CancellationTokenSource Stop = new();
        public readonly List<Flight> Members = [];
        public readonly TaskCompletionSource Retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly Dictionary<CdnKey, byte[]> archives = [];
    private int activeGroups;
    public int ActiveGroups { get { lock (gate) return activeGroups; } }
    public Func<CancellationToken, Task>? BeforeArchiveAdmission { get; set; }
    private readonly List<ArchiveReceipt> archiveReceipts = [];
    public Func<ArchiveReceipt, CancellationToken, Task>? BeforeRangeFetch { get; set; }
    public IReadOnlyList<ArchiveReceipt> ArchiveRequests { get { lock (gate) return archiveReceipts.ToArray(); } }
    public InMemoryLibraryObjectAccess AddArchive(CdnKey archive, ReadOnlyMemory<byte> bytes)
    { lock (gate) { archives[archive] = bytes.ToArray(); return this; } }

    private bool TrySourceBytes(ObjectRequest request, CdnContext context, out byte[] bytes)
    {
        if (request.Locations is not { Count: > 0 }) return available.TryGetValue(request.Key, out bytes!);
        var rank = new Dictionary<CdnKey, int>();
        foreach (var key in context.Archives ?? []) rank.TryAdd(key, rank.Count);
        var location = request.Locations.OrderBy(at => rank.GetValueOrDefault(at.Archive, int.MaxValue)).ThenBy(at => at.Archive).ThenBy(at => at.Offset).First();
        if (archives.TryGetValue(location.Archive, out var archive) && location.Offset >= 0 && location.Size > 0 && location.Offset <= archive.LongLength - location.Size)
        { bytes = archive.AsSpan((int)location.Offset, (int)location.Size).ToArray(); return true; }
        bytes = []; return false;
    }
    private ArchiveGroup[] PrepareArchiveGroups(Claim claim, ObjectRequest[] requests, CdnContext? context, FetchPriority priority)
    {
        if (context is null) return [];
        var qualified = requests.Where(request => request.Key.Type == ObjectKeyType.Encoded && request.Key.Namespace == 0 && request.Locations is { Count: > 0 } && request.Locations.All(at => at.Size <= options.InMemoryObjectBytes)).ToArray();
        if (qualified.Length == 0) return [];
        var byKey = qualified.ToDictionary(request => new CdnKey(request.Key.KeyBytes()));
        var plan = RangePlanner.Plan(qualified.Select(request => new PlanObject(new(request.Key.KeyBytes()), request.DeclaredLength, request.Locations!)), context.Archives, options.Ranges);
        var result = new List<ArchiveGroup>();
        foreach (var range in plan.Ranges)
        {
            var segment = new List<PlannedMember>();
            void Flush()
            {
                if (segment.Count == 0) return;
                if (activeGroups >= limits.MaxGroups || flights.Count + segment.Count > limits.MaxFlights) { segment.Clear(); return; }
                var begin = segment[0].OffsetInRange; var end = segment[^1].OffsetInRange + segment[^1].Size;
                var clipped = new PlannedRange(range.Archive, range.Offset + begin, end - begin, segment.Select(member => member with { OffsetInRange = member.OffsetInRange - begin }).ToArray());
                var group = new ArchiveGroup(claim, clipped, context, Streams.NewTicket(priority));
                foreach (var member in segment)
                {
                    var request = byKey[member.EKey]; var flight = new Flight(request, context, claim, group.Ticket) { Group = group, Work = group.Retired.Task };
                    group.Members.Add(flight); flights.Add(request.Key, flight);
                }
                result.Add(group); activeGroups++; segment.Clear();
            }
            foreach (var member in range.Members)
            {
                var request = byKey[member.EKey];
                if (stored.ContainsKey(request.Key) || flights.ContainsKey(request.Key)) Flush();
                else segment.Add(member);
            }
            Flush();
        }
        return result.ToArray();
    }
    private Task<ObjectResult> GetPreparedBatchAsync(Claim claim, ObjectRequest request, CdnContext? context, FetchPriority priority, CancellationToken ct)
    {
        if (request.Locations is { Count: > 0 })
        {
            var validation = RangePlanner.Plan([new(new(request.Key.KeyBytes()), request.DeclaredLength, request.Locations)], context?.Archives, options.Ranges);
            if (validation.Rejected.Count != 0) return Task.FromResult(new ObjectResult(request.Key, ObjectState.Refused, Code: validation.Rejected[0].Code));
        }
        lock (gate)
            if (request.Locations is { Count: > 0 } && !stored.ContainsKey(request.Key) && !flights.ContainsKey(request.Key) && activeGroups >= limits.MaxGroups)
                return Task.FromResult(new ObjectResult(request.Key, ObjectState.StoreFailed, Code: LibraryObjectAccessCodes.Busy));
        return GetAsync(claim, request, context, priority, false, ct);
    }
    private async Task StopFlightAsync(Flight flight)
    {
        try { await flight.Stop.CancelAsync(); } catch (Exception) { }
        if (flight.Group is not { } group) { await flight.Work; return; }
        bool final; lock (gate) final = group.Members.All(member => member.Consumers == 0);
        if (final) { try { await group.Stop.CancelAsync(); } catch (Exception) { } await group.Retired.Task; }
    }
    private async Task ProduceArchiveAsync(ArchiveGroup group)
    {
        try
        {
            using var stream = await Streams.AcquireAsync(group.Ticket, group.Stop.Token);
            var segments = new List<(long Offset, long Length, Flight[] Members)>();
            lock (gate)
            {
                var segment = new List<Flight>();
                void Flush()
                {
                    if (segment.Count == 0) return;
                    var first = group.Range.Members.Single(at => at.EKey.Equals(new CdnKey(segment[0].Request.Key.KeyBytes())));
                    var last = group.Range.Members.Single(at => at.EKey.Equals(new CdnKey(segment[^1].Request.Key.KeyBytes())));
                    segments.Add((group.Range.Offset + first.OffsetInRange, last.OffsetInRange + last.Size - first.OffsetInRange, segment.ToArray())); segment.Clear();
                }
                foreach (var flight in group.Members)
                {
                    if (flight.LiveConsumers.Count == 0) { Flush(); flight.Done.TrySetCanceled(); }
                    else if (stored.TryGetValue(flight.Request.Key, out var existing))
                    { Flush(); flight.Done.TrySetResult(new(flight.Request.Key, ObjectState.Present, ServedFrom.Store, existing.Origin, existing.Bytes.LongLength)); }
                    else segment.Add(flight);
                }
                Flush();
            }
            foreach (var segment in segments)
            {
                var receipt = new ArchiveReceipt(group.Range.Archive, segment.Offset, segment.Length, group.Context);
                lock (gate) { archiveReceipts.Add(receipt); fetched.Add((segment.Members[0].Request.Key, group.Context)); }
                if (BeforeRangeFetch is { } before) await before(receipt, group.Stop.Token);
                group.Stop.Token.ThrowIfCancellationRequested();
                var published = new List<ObjectKey>();
                lock (gate)
                {
                    var complete = archives.TryGetValue(group.Range.Archive, out var archive) && segment.Offset <= archive.LongLength - segment.Length;
                    if (complete) group.Transferred += segment.Length;
                    foreach (var flight in segment.Members)
                    {
                        var key = flight.Request.Key; var member = group.Range.Members.Single(at => at.EKey.Equals(new CdnKey(key.KeyBytes())));
                        if (flight.LiveConsumers.Count == 0) { flight.Done.TrySetCanceled(); continue; }
                        ObjectResult result;
                        if (!complete) result = new(key, ObjectState.Unavailable, Code: "CDN_RANGE_MISMATCH");
                        else
                        {
                            var bytes = archive!.AsSpan((int)(group.Range.Offset + member.OffsetInRange), (int)member.Size).ToArray();
                            var refusal = ObjectVerification.Default.Check(key, bytes, flight.Request.DeclaredLength ?? member.Size);
                            var actualOwner = flight.LiveConsumers.FirstOrDefault(consumer => consumer.MinimumLength <= bytes.LongLength && (consumer.DeclaredLength is null || consumer.DeclaredLength == bytes.LongLength));
                            if (refusal is not null) result = new(key, ObjectState.Refused, Code: refusal.Code);
                            else if (actualOwner is null) result = new(key, ObjectState.Refused, Code: ObjectRefusalCodes.SizeMismatch);
                            else
                            {
                                if (stored.TryAdd(key, new(bytes, ObjectOrigin.Cdn))) published.Add(key);
                                Credit(actualOwner.Claim, key); result = new(key, ObjectState.Present, ServedFrom.Cdn, stored[key].Origin, bytes.LongLength);
                            }
                        }
                        flight.Done.TrySetResult(result);
                    }
                }
                if (published.Count != 0) commits.Publish(published, []);
            }
            stream.Complete(null);
        }
        catch (OperationCanceledException) { foreach (var member in group.Members) member.Done.TrySetCanceled(); }
        catch (Exception error) { foreach (var member in group.Members) member.Done.TrySetException(error); }
        finally
        {
            lock (gate)
            {
                activeGroups--;
                foreach (var member in group.Members) if (flights.TryGetValue(member.Request.Key, out var current) && ReferenceEquals(current, member)) flights.Remove(member.Request.Key);
            }
            group.Retired.TrySetResult();
        }
    }
}
