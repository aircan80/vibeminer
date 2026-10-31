using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibeMiner.Infrastructure;

namespace VibeMiner.Backend.Tests;

/// <summary>
/// The pinned WoWDBDefs definition files as the repository holds them (#144): sources.json and the Brotli stream it
/// describes, decoded here without the engine's reader, so tests can check that reader against the files and derive
/// authored variants from real definitions.
/// </summary>
internal static class PinnedDefinitionFiles
{
    internal static string Directory => RepositoryPaths.File($"src/backend/VibeMiner.Infrastructure/Tables/Resources/WoWDBDefs/{BundledTableDefinitions.Revision}");

    private static readonly Lazy<IReadOnlyDictionary<string, byte[]>> Files = new(() =>
    {
        using var sources = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Directory, "sources.json")));
        var listed = sources.RootElement.GetProperty("definitions").GetProperty("files").EnumerateArray()
            .Select(file => (Path: file.GetProperty("path").GetString()!, Sha256: file.GetProperty("sha256").GetString()!, Length: file.GetProperty("byteLength").GetInt32())).ToArray();
        using var stream = new BrotliStream(File.OpenRead(Path.Combine(Directory, "definitions.br")), CompressionMode.Decompress);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (path, sha256, length) in listed)
        {
            var bytes = new byte[length]; stream.ReadExactly(bytes);
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != sha256) throw new InvalidDataException(path + " doesn't match sources.json.");
            files.Add(path["definitions/".Length..^".dbd".Length], bytes);
        }
        if (stream.Read(new byte[1]) != 0) throw new InvalidDataException("definitions.br has bytes past its last listed file.");
        return files;
    });

    internal static IReadOnlyCollection<string> Names => (IReadOnlyCollection<string>)Files.Value.Keys;
    internal static byte[] Bytes(string name) => Files.Value[name];
    internal static string Text(string name) => Encoding.UTF8.GetString(Bytes(name));
}
