using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibeMiner.Files.Names;
using VibeMiner.ObjectAccess;
using VibeMiner.ObjectAccess.Testing;

namespace VibeMiner.Files.Tests;

/// <summary>
/// The names switch of a real <see cref="FileSystem"/> in tests, and a way to load authored names into it the way the engine
/// does: a wow-listfile release scripted in the layer 2 fake, then a forced <see cref="IFileNames.UpdateAsync"/>.
/// </summary>
internal sealed class RealNames : IFileNameSwitch
{
    private int release;

    public bool On { get; set; }

    public Task<bool> NamesEnabledAsync(CancellationToken cancellationToken) => Task.FromResult(On);

    /// <summary>Turns names on and loads exactly <paramref name="names"/> as a new release.</summary>
    public async Task UseAsync(IFileSystem files, InMemoryObjectSource objects, IReadOnlyDictionary<uint, string> names)
    {
        On = true;
        var csv = Encoding.UTF8.GetBytes(string.Concat(names.OrderBy(n => n.Key).Select(n => $"{n.Key};{n.Value}\n")));
        var tag = "r" + (++release).ToString(CultureInfo.InvariantCulture);
        objects.AddListfile(tag, Release(tag, csv), csv);
        var status = await files.Names.UpdateAsync(true, default);
        if (status.State != FileNamesState.Ready || status.Tag != tag) throw new InvalidOperationException($"Names didn't load: {status}.");
    }

    public static byte[] Release(string tag, byte[] asset) => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
    {
        ["tag_name"] = tag,
        ["published_at"] = "2026-10-28T19:21:00Z",
        ["assets"] = new object[]
        {
            new Dictionary<string, object?>
            {
                ["name"] = WowListfileObservations.AssetName,
                ["size"] = asset.Length,
                ["browser_download_url"] = WowListfileObservations.AssetUrl(tag),
                ["digest"] = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(asset)),
            },
        },
    });
}
