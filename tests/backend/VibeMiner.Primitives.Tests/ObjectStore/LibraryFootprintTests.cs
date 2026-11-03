using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;
using static VibeMiner.Primitives.Tests.LibraryFootprintTestKit;
using static VibeMiner.Primitives.Tests.ObjectStoreTestKit;

namespace VibeMiner.Primitives.Tests;

public sealed class LibraryFootprintTests
{
    private static LibraryFootprintBucket Sum(IReadOnlyDictionary<string, (long Length, string? Hash)> files, IEnumerable<string> paths)
    { var names = paths.ToArray(); return new(names.LongLength, names.Sum(path => files[path].Length)); }
    [Fact]
    public async Task MixedPacksSidecarsStagingAndUnknownFilesReconcileWithoutPayloadOrDatabaseMutation()
    {
        if (!Supported) return;
        using var temp = new TempDirectory(); var root = DirectoryPath(temp); await using var library = await ObjectStore.OpenLibraryAsync(root, Quick); await SeedMixedAsync(library);
        var realPacks = PackFiles(root).Select(path => Path.GetRelativePath(root, path)).ToArray();
        WriteSized(root, "packs/pack-000099.vmp", 31); WriteSized(root, "staging/one.part", 23); WriteSized(root, "staging/two.part", 29);
        foreach (var path in new[] { "pack-000002.vmp", "packs/pack-000000.vmp", "packs/pack-1234567.vmp", "packs/nested/pack-000098.vmp", "staging/TEMP.PART", "staging/nested/three.part", "objects.sqlite3-wal.extra", "other.bin" }) WriteSized(root, path, 17);
        var members = Membership(root); var rows = Rows(root); var before = CaptureFiles(root); var packs = realPacks.Append(Path.Combine("packs", "pack-000099.vmp")).ToArray();
        var database = new[] { "objects.sqlite3" }; var wal = before.Keys.Where(path => path == "objects.sqlite3-wal").ToArray(); var shm = before.Keys.Where(path => path == "objects.sqlite3-shm").ToArray();
        var staging = new[] { Path.Combine("staging", "one.part"), Path.Combine("staging", "two.part") }; var known = packs.Concat(database).Concat(wal).Concat(shm).Concat(staging).ToHashSet(StringComparer.Ordinal);
        await using var view = library.ForFootprint();
        // Even a recognized pack containing authored junk is metadata, and can be exclusively locked against payload readers.
        using (var exclusive = new FileStream(Path.Combine(root, "packs", "pack-000099.vmp"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var sample = await view.MeasureAsync(); Assert.True(sample.Complete); Assert.Empty(sample.Problems); AssertReconciled(sample);
            Assert.Equal(Sum(before, packs), sample.Packs); Assert.Equal(Sum(before, database), sample.Database); Assert.Equal(Sum(before, wal), sample.DatabaseWal); Assert.Equal(Sum(before, shm), sample.DatabaseShm); Assert.Equal(Sum(before, staging), sample.Staging);
            Assert.Equal(Sum(before, before.Keys.Where(path => !known.Contains(path))), sample.Other); Assert.Equal(Sum(before, before.Keys), sample.Total);
        }
        Assert.Equal(before.OrderBy(pair => pair.Key), CaptureFiles(root).OrderBy(pair => pair.Key)); Assert.Equal(members, Membership(root)); Assert.Equal(rows, Rows(root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrCorruptPackPayloadDoesNotTriggerIndexReadsRepairOrInferredPhysicalMembership(bool missing)
    {
        if (!Supported) return;
        using var temp = new TempDirectory(); var root = DirectoryPath(temp); await using var library = await ObjectStore.OpenLibraryAsync(root, Quick); await SeedMixedAsync(library);
        var pack = Assert.Single(PackFiles(root)); if (missing) File.Delete(pack); else Flip(pack, PackFormat.PackHeaderBytes + PackFormat.EntryHeaderBytes + 30);
        var members = Membership(root); var rows = Rows(root); await using var view = library.ForFootprint();
        // Verification queries can change SQLite read marks; bracket only the metadata sample with file hashes.
        var before = CaptureFiles(root); var sample = await view.MeasureAsync(); var after = CaptureFiles(root);
        Assert.True(sample.Complete); AssertReconciled(sample); Assert.Equal(missing ? 0 : 1, sample.Packs.Files);
        Assert.Equal(missing ? 0 : new FileInfo(pack).Length, sample.Packs.Bytes); Assert.Equal(before.OrderBy(pair => pair.Key), after.OrderBy(pair => pair.Key)); Assert.Equal(members, Membership(root)); Assert.Equal(rows, Rows(root));
    }

    [Theory]
    [InlineData("packs")]
    [InlineData("staging")]
    [InlineData("objects.sqlite3")]
    public async Task MissingRequiredPathsAreIncompleteAndAreNotRecreated(string relative)
    {
        if (!Supported) return;
        using var temp = new TempDirectory(); var root = DirectoryPath(temp); await using var library = await ObjectStore.OpenLibraryAsync(root, Quick); await using var view = library.ForFootprint();
        var path = Path.Combine(root, relative); var held = Path.Combine(PhysicalPath(temp.Path), "held"); var directory = Directory.Exists(path);
        if (directory) Directory.Move(path, held); else File.Move(path, held);
        try
        {
            var sample = await view.MeasureAsync(); Assert.False(sample.Complete); AssertReconciled(sample);
            Assert.Contains(sample.Problems, problem => problem.RelativePath == relative && problem.Code == LibraryFootprintProblemCode.MissingRequiredPath); Assert.False(Path.Exists(path));
        }
        finally { if (directory) Directory.Move(held, path); else File.Move(held, path); }
    }

    [Fact]
    public async Task OptionalAbsentSidecarsAndMarkerHaveMeasuredZeroWithoutInventingRequiredPaths()
    {
        if (!Supported) return;
        using var temp = new TempDirectory(); var root = DirectoryPath(temp); await using var library = await ObjectStore.OpenLibraryAsync(root, Quick); await using var view = library.ForFootprint();
        var moved = new List<(string Original, string Held)>();
        try
        {
            foreach (var name in new[] { "objects.sqlite3-wal", "objects.sqlite3-shm", "library-v1" })
            {
                var original = Path.Combine(root, name); if (!File.Exists(original)) continue; var held = Path.Combine(PhysicalPath(temp.Path), name + ".held"); File.Move(original, held); moved.Add((original, held));
            }
            var sample = await view.MeasureAsync(); Assert.True(sample.Complete); Assert.Equal(new LibraryFootprintBucket(0, 0), sample.DatabaseWal); Assert.Equal(new LibraryFootprintBucket(0, 0), sample.DatabaseShm); AssertReconciled(sample);
            foreach (var (original, _) in moved) Assert.False(File.Exists(original));
        }
        finally { foreach (var (original, held) in moved) File.Move(held, original); }
    }

    [Fact]
    public async Task MetadataDenialAndDisappearanceAreExplicitAndDoNotPoisonLaterSamples()
    {
        if (!Supported) return;
        using var temp = new TempDirectory(); var root = DirectoryPath(temp); await using var library = await ObjectStore.OpenLibraryAsync(root, Quick);
        WriteSized(root, "denied.bin", 11); WriteSized(root, "vanishing.bin", 13); var armed = true;
        await using var view = library.ForFootprint(new() { Fault = (point, path) =>
        {
            if (!armed || point != LibraryFootprintPoint.BeforeEntryMetadata) return;
            if (path == "denied.bin") throw new UnauthorizedAccessException("Authored metadata denial.");
            if (path == "vanishing.bin") File.Delete(Path.Combine(root, path));
        } });
        var sample = await view.MeasureAsync(); Assert.False(sample.Complete); AssertReconciled(sample);
        Assert.Contains(sample.Problems, problem => problem.RelativePath == "denied.bin" && problem.Code == LibraryFootprintProblemCode.Unreadable);
        Assert.Contains(sample.Problems, problem => problem.RelativePath == "vanishing.bin" && problem.Code == LibraryFootprintProblemCode.Disappeared);
        armed = false; Assert.True((await view.MeasureAsync()).Complete); Assert.True(File.Exists(Path.Combine(root, "denied.bin"))); Assert.False(File.Exists(Path.Combine(root, "vanishing.bin")));
    }

    [Fact]
    public async Task FileDirectoryAndRecognizedNameLinksNeverContributeOutsideBytesOrGetFollowed()
    {
        if (!Supported) return;
        using var temp = new TempDirectory(); var root = DirectoryPath(temp); var outside = Path.Combine(PhysicalPath(temp.Path), "outside"); Directory.CreateDirectory(outside); WriteSized(outside, "secret.bin", 12345);
        await using var library = await ObjectStore.OpenLibraryAsync(root, Quick); await using var view = library.ForFootprint(); var before = await view.MeasureAsync();
        Directory.CreateSymbolicLink(Path.Combine(root, "outside-link"), outside); File.CreateSymbolicLink(Path.Combine(root, "packs", "pack-000099.vmp"), Path.Combine(outside, "secret.bin")); File.CreateSymbolicLink(Path.Combine(root, "staging", "borrowed.part"), Path.Combine(outside, "secret.bin"));
        try
        {
            var sample = await view.MeasureAsync(); Assert.False(sample.Complete); Assert.Equal(before.Total, sample.Total); Assert.Equal(Buckets(before), Buckets(sample)); AssertReconciled(sample);
            foreach (var path in new[] { "outside-link", "packs/pack-000099.vmp", "staging/borrowed.part" }) Assert.Contains(sample.Problems, problem => problem.RelativePath == path && problem.Code == LibraryFootprintProblemCode.Link);
            Assert.Equal(12345, new FileInfo(Path.Combine(outside, "secret.bin")).Length);
        }
        finally { Directory.Delete(Path.Combine(root, "outside-link")); File.Delete(Path.Combine(root, "packs", "pack-000099.vmp")); File.Delete(Path.Combine(root, "staging", "borrowed.part")); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RootMissingOrReplacedWithAnOutsideLinkCannotChangeTheCapturedLibraryDirectory(bool link)
    {
        if (!Supported) return;
        using var temp = new TempDirectory(); var root = DirectoryPath(temp); var held = Path.Combine(PhysicalPath(temp.Path), "held"); var outside = Path.Combine(PhysicalPath(temp.Path), "outside"); Directory.CreateDirectory(outside); WriteSized(outside, "objects.sqlite3", 12345);
        await using var library = await ObjectStore.OpenLibraryAsync(root, Quick); await using var view = library.ForFootprint(); Directory.Move(root, held); if (link) Directory.CreateSymbolicLink(root, outside);
        try
        {
            var sample = await view.MeasureAsync(); Assert.False(sample.Complete); Assert.Equal(new LibraryFootprintBucket(0, 0), sample.Total); AssertReconciled(sample);
            Assert.Contains(sample.Problems, problem => problem.RelativePath == "" && problem.Code == (link ? LibraryFootprintProblemCode.Link : LibraryFootprintProblemCode.MissingRequiredPath));
            Assert.Equal(12345, new FileInfo(Path.Combine(outside, "objects.sqlite3")).Length);
        }
        finally { if (link) Directory.Delete(root); Directory.Move(held, root); }
    }
}
