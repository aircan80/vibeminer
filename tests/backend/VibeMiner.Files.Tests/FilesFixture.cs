using System.Globalization;
using VibeMiner.Builds;
using VibeMiner.Cdn;
using VibeMiner.Files.Testing;
using VibeMiner.ObjectAccess.Testing;
using VibeMiner.Objects;
using L3 = VibeMiner.Builds.Testing;

namespace VibeMiner.Files.Tests;

/// <summary>
/// Builds the same synthetic build for the fake and for the real <see cref="FileSystem"/> over the layer 3 and layer 2 fakes
/// (D137): no object store, no sockets. Every file is one ROOT variant.
/// </summary>
internal static class FilesFixture
{
    public static readonly BuildRef Build = new("wow", new string('a', 32), new string('b', 32));
    public static readonly BuildRef Other = new("wow", new string('c', 32), new string('d', 32));

    /// <summary>The content key the fake and the fixture give a file without one.</summary>
    public static string ContentKeyOf(uint id) => id.ToString("x32", CultureInfo.InvariantCulture);

    /// <summary>A distinct encoded key per file, never equal to its content key.</summary>
    public static CdnKey EKeyOf(uint id) => CdnKey.Parse("e" + id.ToString("x31", CultureInfo.InvariantCulture));

    public static ObjectKey ObjectKeyOf(uint id) => new(ObjectKeyType.Encoded, EKeyOf(id).ToBytes());

    public static uint LocaleMask(IReadOnlyList<string>? locales) =>
        (locales ?? [.. FileLocales.Names]).Aggregate(0u, (mask, l) => mask | FileLocales.BitOf(l));

    public static L3.FakeFile ToLayer3(FakeFile f)
    {
        var flags = f.Encrypted ? 0x8000000UL : 0UL;
        var variant = new FileVariant(LocaleMask(f.Locales), flags, CdnKey.Parse(f.ContentKey ?? ContentKeyOf(f.FileDataId)), f.Size,
            f.NotInEncoding ? null : EKeyOf(f.FileDataId), f.NotInEncoding ? null : 64, null);
        return new(f.FileDataId, f.Path, [variant]);
    }

    public static BuildMetadata Metadata(IReadOnlyList<string> locales, int fileCount) =>
        new("WOW-1.0.0.1", CdnKey.Parse(new string('1', 32)), CdnKey.Parse(new string('2', 32)), CdnKey.Parse(new string('3', 32)), 0,
            fileCount, locales);

    /// <summary>The real file system over fakes, holding exactly the given files; stored ones are in layer 2's store.</summary>
    public static (FileSystem Files, L3.InMemoryBuilds Builds, InMemoryObjectSource Objects) Real(BuildRef build, IReadOnlyList<string> locales,
        IReadOnlyList<FakeFile> files, FileSystemOptions? options = null)
    {
        var builds = new L3.InMemoryBuilds();
        builds.Add(build, new L3.FakeBuild(Metadata(locales, files.Count), files.Select(ToLayer3).ToArray()));
        var objects = new InMemoryObjectSource();
        foreach (var f in files)
            if (f.Storage == FileStorage.Stored && !f.NotInEncoding) objects.AddStored(ObjectKeyOf(f.FileDataId), [1, 2, 3]);
        return (new FileSystem(builds, objects, options), builds, objects);
    }
}
