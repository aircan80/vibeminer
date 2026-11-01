using System.Text.Json;
using System.Text.Json.Serialization;
using VibeMiner.Objects;

namespace VibeMiner.Cli.Profile;

internal enum ScrubOutcome { Clean, Problem }

/// <summary>
/// The outcome of the last finished background scrub, kept in <c>scrub-v1.json</c> in the data folder. <see cref="Problems"/> holds
/// the first few corrupt and lost keys; the counts are the full totals. <see cref="FailureCode"/> and <see cref="FailureMessage"/>
/// are set when the scrub threw instead of reporting.
/// </summary>
internal sealed record ScrubRecord(
    int Version,
    DateTimeOffset FinishedAt,
    ScrubOutcome Outcome,
    string? IndexProblem,
    bool IndexRebuilt,
    bool IndexChecked,
    long Checked,
    long Bytes,
    long CorruptCount,
    long LostCount,
    IReadOnlyList<string> Problems,
    string? FailureCode,
    string? FailureMessage)
{
    public const int CurrentVersion = 1;
    public const int MaxProblems = 20;
    public const int MaxText = 1000;

    public static ScrubRecord FromReport(ScrubReport report, DateTimeOffset finishedAt)
    {
        var verify = report.Verify;
        var problems = verify.Corrupt.Select(key => $"corrupt {key}").Concat(verify.Lost.Select(key => $"lost {key}")).Take(MaxProblems).ToArray();
        var clean = report.IndexChecked && report.IndexProblem is null && !report.IndexRebuilt && verify.CorruptCount == 0 && verify.LostCount == 0;
        var indexProblem = report.IndexProblem ?? (report.IndexChecked ? null : "The index was not checked: its writer kept committing.");
        return new(CurrentVersion, finishedAt, clean ? ScrubOutcome.Clean : ScrubOutcome.Problem, Clip(indexProblem), report.IndexRebuilt,
            report.IndexChecked, verify.Checked, verify.Bytes, verify.CorruptCount, verify.LostCount, problems, null, null);
    }

    public static ScrubRecord FromFailure(Exception error, DateTimeOffset finishedAt) =>
        new(CurrentVersion, finishedAt, ScrubOutcome.Problem, null, false, false, 0, 0, 0, 0, [],
            error is ObjectStoreException store ? store.Code : error.GetType().FullName, Clip(error.Message));

    private static string? Clip(string? text) => text is { Length: > MaxText } ? text[..MaxText] : text;
}

internal enum ScrubRecordState
{
    /// <summary>No record: no scrub has finished in this data folder.</summary>
    Absent,
    Present,
    /// <summary>The file is not valid JSON or not a version 1 record.</summary>
    BadJson,
    /// <summary>A newer VibeMiner wrote it.</summary>
    Newer,
    /// <summary>Larger than <see cref="ScrubRecords.MaxBytes"/>; not read.</summary>
    TooLarge,
    /// <summary>The file exists but could not be read.</summary>
    Unreadable,
}

/// <summary>A read of the record. <see cref="Record"/> is set only for <see cref="ScrubRecordState.Present"/>.</summary>
internal sealed record ScrubRecordRead(ScrubRecordState State, ScrubRecord? Record = null, string? Reason = null);

internal static class ScrubRecords
{
    public const string FileName = "scrub-v1.json";
    public const int MaxBytes = 64 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string PathIn(string folder) => Path.Combine(folder, FileName);

    /// <summary>Reads the record. Never throws for a missing, malformed, newer, oversized or unreadable file; only for cancellation.</summary>
    public static async Task<ScrubRecordRead> ReadAsync(string folder, CancellationToken ct = default)
    {
        byte[] bytes;
        try
        {
            await using var stream = new FileStream(PathIn(folder), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
            if (stream.Length > MaxBytes) return new(ScrubRecordState.TooLarge, Reason: $"The scrub record is larger than {MaxBytes} bytes.");
            bytes = new byte[stream.Length];
            await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return new(ScrubRecordState.Absent); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(ScrubRecordState.Unreadable, Reason: e.Message); }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version))
                return new(ScrubRecordState.BadJson, Reason: "The scrub record has no version.");
            if (version > ScrubRecord.CurrentVersion) return new(ScrubRecordState.Newer, Reason: $"The scrub record is version {version}, from a newer VibeMiner.");
            if (version != ScrubRecord.CurrentVersion) return new(ScrubRecordState.BadJson, Reason: $"The scrub record has unknown version {version}.");
            var record = root.Deserialize<ScrubRecord>(Json);
            if (record is null) return new(ScrubRecordState.BadJson, Reason: "The scrub record is empty.");
            if (record.Problems.Count > ScrubRecord.MaxProblems)
                return new(ScrubRecordState.BadJson, Reason: "The scrub record lists too many problems.");
            if (record.Problems.Any(p => p is null))
                return new(ScrubRecordState.BadJson, Reason: "The scrub record has a null problem entry.");
            return new(ScrubRecordState.Present, record);
        }
        catch (JsonException e) { return new(ScrubRecordState.BadJson, Reason: "The scrub record is not valid JSON: " + e.Message); }
    }

    /// <summary>Writes the record to a temp file, flushes it to disk, then replaces the old record in one rename.</summary>
    public static async Task WriteAsync(string folder, ScrubRecord record, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, Json);
        if (bytes.Length > MaxBytes) throw new InvalidOperationException("The scrub record is too large to write.");
        // One writer per data folder, so one temp name; a leftover from a killed write is replaced here.
        var temp = Path.Combine(folder, FileName + ".tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, PathIn(folder), overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
