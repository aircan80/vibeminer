using System.Text.Json;
using VibeMiner.Files.Names;

namespace VibeMiner.Cli.Browse;

/// <summary>
/// The community file names switch (D105) as the app saved it: <c>values.communityListfile</c> in <c>settings-v1.json</c> in the
/// data folder (the format of Infrastructure's JsonSettingsStore). Read on every call, read-only. No file, no <c>values</c> or no
/// key is the app's default, on. Anything it can't trust reads as off (fail closed): a file over 64 KiB, invalid JSON or UTF-8,
/// no version or a newer one, a repeated top-level name or setting, a value that isn't true or false, or a file that can't be opened.
/// </summary>
internal sealed class SettingsNameSwitch(string dataFolder) : IFileNameSwitch
{
    public const string FileName = "settings-v1.json";
    public const int MaxFileBytes = 64 * 1024;
    public const int FormatVersion = 1;
    public const string Key = "communityListfile";
    /// <summary>The app's default for the switch (SettingsService.CommunityListfileDefault).</summary>
    public const bool Default = true;

    public string FilePath { get; } = Path.Combine(dataFolder, FileName);

    public async Task<bool> NamesEnabledAsync(CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            if (stream.Length > MaxFileBytes) return false;
            bytes = new byte[stream.Length];
            var read = 0;
            while (read < bytes.Length)
            {
                var count = await stream.ReadAsync(bytes.AsMemory(read), cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                read += count;
            }
            // A file that changed size while it was read is not trusted.
            if (read != bytes.Length || await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0) return false;
        }
        catch (FileNotFoundException) { return Default; }
        catch (DirectoryNotFoundException) { return Default; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        return Read(bytes);
    }

    /// <summary>The switch from the file's bytes, by the rules above.</summary>
    internal static bool Read(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            // A repeated name at the top is refused, as the app's store does: TryGetProperty would take one copy silently.
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject()) if (!names.Add(property.Name)) return false;
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number) return false;
            var text = version.GetRawText();
            if (text != FormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)) return false;
            if (!root.TryGetProperty("values", out var values)) return Default;
            if (values.ValueKind != JsonValueKind.Object) return false;
            bool? value = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in values.EnumerateObject())
            {
                if (!seen.Add(property.Name)) return false;
                if (property.Name != Key) continue;
                if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
                value = property.Value.GetBoolean();
            }
            return value ?? Default;
        }
        catch (JsonException) { return false; }
        // Invalid UTF-8 or a lone surrogate escape surfaces only when a name or string is decoded.
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return false; }
    }
}
