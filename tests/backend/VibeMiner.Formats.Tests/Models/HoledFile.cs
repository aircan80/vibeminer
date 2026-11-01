using VibeMiner.Builds;
using VibeMiner.Files;
using VibeMiner.Files.Testing;
using Xunit;

namespace VibeMiner.Formats.Tests.Models;

// A layer 4 stream over authored bytes whose last byte is a hole under an unknown key; replaces the legacy BLTE projection
// stream the old tests built. Dispose the files after the stream.
internal static class HoledFile
{
    private static readonly BuildRef Build = new("wow", new string('a', 32), new string('b', 32));

    internal static async Task<(IBuildFiles Files, FileReadStream Stream)> OpenAsync(byte[] bytes)
    {
        var opened = await new InMemoryFileSystem()
            .Add(Build, ["enUS"], [new FakeFile(1, Content: bytes, Holes: [new FileHole(bytes.Length - 1, 1, "0123456789ABCDEF")])])
            .ForAsync(Build, "enUS", default);
        var open = await opened.Files!.OpenReadAsync(FileRef.Id(1), default);
        Assert.Equal(FileReadState.Partial, open.State);
        Assert.NotEmpty(open.Stream!.Holes);
        return (opened.Files, open.Stream);
    }
}
