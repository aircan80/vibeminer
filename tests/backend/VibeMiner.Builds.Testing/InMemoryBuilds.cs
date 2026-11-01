using VibeMiner.Builds.Formats;
using VibeMiner.ObjectAccess;

namespace VibeMiner.Builds.Testing;

/// <summary>One file of a fake build: its FileDataID, its path when ROOT names it, and its variants in ROOT order.</summary>
public sealed record FakeFile(uint FileDataId, string? Path, IReadOnlyList<FileVariant> Variants);

/// <summary>A fake build's metadata (locales included), files and CDN context.</summary>
public sealed record FakeBuild(BuildMetadata Metadata, IReadOnlyList<FakeFile> Files, CdnContext? Cdn = null);

/// <summary>
/// The standard <see cref="IBuilds"/> test double for layer 4 and up: no layer 2, no files. Builds are added or scripted to
/// fail; anything else answers NeedsNetwork on the build config, as the real opener does offline. Reuse, shared opens and
/// cancellation follow the same rules as <see cref="BuildOpener"/>.
/// </summary>
public sealed class InMemoryBuilds : IBuilds
{
    private readonly Lock gate = new();
    private readonly Dictionary<BuildRef, FakeBuild> builds = [];
    private readonly Dictionary<BuildRef, BuildOpenResult> failures = [];
    private readonly OpenCache cache;
    private int opens;

    public InMemoryBuilds(int maxOpenBuilds = 4) => cache = new OpenCache(maxOpenBuilds);

    /// <summary>When set, every open that isn't served from the cache waits for this first (cancellation still ends the wait).</summary>
    public Func<BuildRef, CancellationToken, Task>? BeforeOpen { get; set; }

    /// <summary>Opens actually run (cache hits and joined opens don't count).</summary>
    public int Opens => Volatile.Read(ref opens);

    public InMemoryBuilds Add(BuildRef build, FakeBuild fake)
    {
        ArgumentNullException.ThrowIfNull(build); ArgumentNullException.ThrowIfNull(fake);
        lock (gate) { builds[build] = fake; failures.Remove(build); }
        return this;
    }

    public InMemoryBuilds Fail(BuildRef build, BuildOpenState state, BuildPart part, string code, DateTimeOffset? observedAt = null)
    {
        ArgumentNullException.ThrowIfNull(build);
        if (state == BuildOpenState.Open) throw new ArgumentException("A failure isn't Open.", nameof(state));
        lock (gate) { failures[build] = new(build, state, null, part, code, $"The {part} is {state} ({code}).", observedAt); builds.Remove(build); }
        return this;
    }

    public Task<BuildOpenResult> OpenAsync(BuildRef build, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(build);
        return cache.OpenAsync(build, token => OpenOnceAsync(build, token), cancellationToken);
    }

    private async Task<BuildOpenResult> OpenOnceAsync(BuildRef build, CancellationToken token)
    {
        Interlocked.Increment(ref opens);
        if (BeforeOpen is { } wait) await wait(build, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (failures.TryGetValue(build, out var failed)) return failed;
            if (builds.TryGetValue(build, out var fake)) return new(build, BuildOpenState.Open, new Opened(build, fake), null, null, null, null);
        }
        return new(build, BuildOpenState.NeedsNetwork, null, BuildPart.BuildConfig, "OBJECT_ACCESS_NEEDS_NETWORK",
            "Not stored, and this source is local only.", null);
    }

    private sealed class Opened(BuildRef build, FakeBuild fake) : IBuild
    {
        private readonly Dictionary<uint, FakeFile> byId = fake.Files.ToDictionary(f => f.FileDataId);
        private readonly Dictionary<ulong, uint> byName = fake.Files.Where(f => f.Path is not null)
            .GroupBy(f => RootNameHash.Of(f.Path!)).ToDictionary(g => g.Key, g => g.Min(f => f.FileDataId));
        private readonly FakeFile[] ordered = fake.Files.OrderBy(f => f.FileDataId).ToArray();

        public BuildRef Build { get; } = build;
        public BuildMetadata Metadata { get; } = fake.Metadata;
        public CdnContext? Cdn { get; } = fake.Cdn;

        public FileResolution Resolve(uint fileDataId)
        {
            if (!byId.TryGetValue(fileDataId, out var file) || file.Variants.Count == 0) return new(FileResolutionState.UnknownFile, null, []);
            var state = file.Variants.Any(v => v.EKey is not null) ? FileResolutionState.Found : FileResolutionState.NotInEncoding;
            return new(state, fileDataId, file.Variants.ToArray());
        }

        public IEnumerable<uint> FileDataIds(string locale, uint fromId = 0) => Walk(BuildLocales.Bit(locale), fromId);

        private IEnumerable<uint> Walk(uint bit, uint fromId)
        {
            foreach (var f in ordered)
                if (f.FileDataId >= fromId && f.Variants.Any(v => (v.LocaleFlags & bit) != 0)) yield return f.FileDataId;
        }

        public FileResolution Resolve(string path)
        {
            ArgumentNullException.ThrowIfNull(path);
            return byName.TryGetValue(RootNameHash.Of(path), out var id) ? Resolve(id) : new(FileResolutionState.UnknownFile, null, []);
        }
    }
}
