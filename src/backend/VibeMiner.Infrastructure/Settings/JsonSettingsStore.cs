using System.Globalization;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

/// <summary>
/// settings-v1.json in the engine data folder: {"version":1,"changedAt":"…","values":{"communityBuildHistory":false,"fetchTableDefinitions":true}}.
/// Reads are bounded and never throw for bad content. Writes go to a temporary file, are flushed, then replace the file.
/// </summary>
public sealed class JsonSettingsStore(string dataDirectory) : ISettingsStore
{
    public const string FileName = "settings-v1.json";
    public const string TemporaryFileName = "settings-v1.json.tmp";
    public const int MaxFileBytes = 64 * 1024;
    private string FilePath => Path.Combine(dataDirectory, FileName);
    private string TemporaryPath => Path.Combine(dataDirectory, TemporaryFileName);

    private static SettingsDocument Unreadable(string message) =>
        new(SettingsFileState.Unreadable, new("SETTINGS_UNREADABLE", message, false), null, null, null, []);
    // The file exists but its content was never parsed, so nothing is known about it and it is never replaced.
    private static SettingsDocument NotRead(string message, bool retryable) =>
        new(SettingsFileState.Unreadable, new("SETTINGS_NOT_READ", message, retryable), null, null, null, []) { Replaceable = false };

    public async Task<SettingsDocument> ReadAsync(CancellationToken token)
    {
        byte[] bytes;
        try
        {
            // Delete sharing lets a write replace the file while a read still has it open on Windows.
            await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
            if (stream.Length > MaxFileBytes) return NotRead("The settings file is larger than VibeMiner allows, so it wasn’t read.", false);
            bytes = new byte[stream.Length];
            var read = 0;
            while (read < bytes.Length)
            {
                var count = await stream.ReadAsync(bytes.AsMemory(read), token);
                if (count == 0) break;
                read += count;
            }
            // A file that grew while it was read is not trusted.
            if (read != bytes.Length || await stream.ReadAsync(new byte[1], token) != 0)
                return NotRead("The settings file changed while it was read.", true);
        }
        catch (FileNotFoundException) { return SettingsDocument.Absent; }
        catch (DirectoryNotFoundException) { return SettingsDocument.Absent; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return NotRead("The settings file couldn’t be opened. It may be locked or not readable.", true);
        }
        return Parse(bytes);
    }

    public static SettingsDocument Parse(ReadOnlyMemory<byte> bytes)
    {
        // A UTF-8 byte order mark is not content; a file saved with one reads like the same file without it.
        if (bytes.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) bytes = bytes[3..];
        try
        {
            // No parse limit below the file bound: a 64 KiB file can't nest deeper than this, so every JsonException
            // is bad content, never a limit of ours, and a newer file is always recognised as newer.
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = MaxFileBytes });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Unreadable("The settings file isn’t a settings object.");
            // Any positive integer is a version, however large: one above this engine's is newer, never unreadable.
            // The version is read first, so nothing else in a newer file (undecodable text included) can make it replaceable.
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !IsPositiveInteger(version.GetRawText()))
                return Unreadable("The settings file has no settings version VibeMiner understands.");
            var versionText = version.GetRawText();
            if (versionText.Length > 9 || int.Parse(versionText, CultureInfo.InvariantCulture) > SettingsService.FormatVersion)
            {
                DateTimeOffset? newerChangedAt;
                try { newerChangedAt = ChangedAt(root); } catch (InvalidOperationException) { newerChangedAt = null; }
                return new(SettingsFileState.Newer, new("SETTINGS_VERSION", $"The settings file is version {versionText}, saved by a newer VibeMiner.", false), newerChangedAt, null, null, []);
            }
            Decode(root);
            var names = new HashSet<string>(StringComparer.Ordinal);
            var fields = new List<KeyValuePair<string, string>>();
            foreach (var property in root.EnumerateObject())
            {
                if (!names.Add(property.Name)) return Unreadable("The settings file repeats a name.");
                if (property.Name is not ("version" or "changedAt" or "values")) fields.Add(new(property.Name, property.Value.GetRawText()));
            }
            var changedAt = ChangedAt(root);
            if (!root.TryGetProperty("values", out var values)) return new(SettingsFileState.Read, null, changedAt, null, null, []) { UnknownFields = fields };
            if (values.ValueKind != JsonValueKind.Object) return Unreadable("The settings file has no readable values.");
            bool? community = null, fetch = null;
            SettingsProblem? communityProblem = null, fetchProblem = null;
            var unknown = new List<KeyValuePair<string, string>>();
            var unreadable = new List<KeyValuePair<string, string>>();
            var valueNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in values.EnumerateObject())
            {
                if (!valueNames.Add(property.Name)) return Unreadable("The settings file repeats a setting.");
                if (property.Name is SettingsService.CommunityBuildHistory or SettingsService.FetchTableDefinitions)
                {
                    bool? value = property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False ? property.Value.GetBoolean() : null;
                    SettingsProblem? problem = null;
                    if (value is null)
                    {
                        problem = new("SETTING_UNREADABLE", $"The saved value for {(property.Name == SettingsService.CommunityBuildHistory ? "Community build history" : "Fetch newer table definitions")} isn’t one VibeMiner understands.", false);
                        unreadable.Add(new(property.Name, property.Value.GetRawText()));
                    }
                    if (property.Name == SettingsService.CommunityBuildHistory) { community = value; communityProblem = problem; }
                    else { fetch = value; fetchProblem = problem; }
                }
                // Every value this version doesn't know is kept; the 64 KiB file bound is the only limit.
                else unknown.Add(new(property.Name, property.Value.GetRawText()));
            }
            return new(SettingsFileState.Read, null, changedAt, community, communityProblem, unknown)
                { UnknownFields = fields, FetchTableDefinitions = fetch, FetchTableDefinitionsProblem = fetchProblem, UnreadableValues = unreadable };
        }
        catch (JsonException) { return Unreadable("The settings file isn’t valid JSON."); }
        // Invalid UTF-8 or a lone surrogate escape in a name or string surfaces only when it is decoded.
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return Unreadable("The settings file isn’t valid UTF-8 text."); }
    }

    // Every name and string must decode, wherever it sits, so undecodable text (a lone surrogate escape) is a damaged
    // file rather than a value kept or rejected one by one. Iterative: a 64 KiB file may nest far deeper than the stack allows.
    private static void Decode(JsonElement root)
    {
        var pending = new Stack<JsonElement>();
        pending.Push(root);
        while (pending.TryPop(out var element))
        {
            if (element.ValueKind == JsonValueKind.String) _ = element.GetString();
            else if (element.ValueKind == JsonValueKind.Object)
                foreach (var property in element.EnumerateObject()) { _ = property.Name; pending.Push(property.Value); }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var item in element.EnumerateArray()) pending.Push(item);
        }
    }

    private static DateTimeOffset? ChangedAt(JsonElement root) =>
        root.TryGetProperty("changedAt", out var changed) && changed.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParseExact(changed.GetString(), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;

    private static bool IsPositiveInteger(string text) => text.Length > 0 && text[0] is >= '1' and <= '9' && text.All(char.IsAsciiDigit);

    public async Task WriteAsync(IReadOnlyList<KeyValuePair<string, string>> values, DateTimeOffset changedAt,
        IReadOnlyList<KeyValuePair<string, string>> unknownFields, CancellationToken token)
    {
        var bytes = Serialize(values, changedAt, unknownFields);
        if (bytes.Length > MaxFileBytes) throw new DiscoveryException("SETTINGS_WRITE_FAILED", "The settings file would be larger than VibeMiner allows. Nothing was changed.");
        try
        {
            await using (var stream = new FileStream(TemporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, token);
                await stream.FlushAsync(token);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(TemporaryPath, FilePath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            try { if (File.Exists(TemporaryPath)) File.Delete(TemporaryPath); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { /* The next write replaces it. */ }
            if (error is OperationCanceledException) throw;
            throw new DiscoveryException("SETTINGS_WRITE_FAILED", "The engine couldn’t write the settings file. Nothing else changed.", true);
        }
    }

    private static byte[] Serialize(IReadOnlyList<KeyValuePair<string, string>> values, DateTimeOffset changedAt,
        IReadOnlyList<KeyValuePair<string, string>> unknownFields)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", SettingsService.FormatVersion);
            writer.WriteString("changedAt", changedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            writer.WriteStartObject("values");
            foreach (var (name, raw) in values)
            {
                writer.WritePropertyName(name);
                // Raw text from the parsed file is valid JSON; revalidating would apply the writer's default depth limit.
                writer.WriteRawValue(raw, skipInputValidation: true);
            }
            writer.WriteEndObject();
            foreach (var (name, raw) in unknownFields)
            {
                writer.WritePropertyName(name);
                writer.WriteRawValue(raw, skipInputValidation: true);
            }
            writer.WriteEndObject();
        }
        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }
}
