using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using VibeMiner.Files;
using VibeMiner.Formats.Definitions;
using VibeMiner.Formats.Generated;

namespace VibeMiner.Formats.Tables;

/// <summary>
/// How reading one of the two key tables went. Only Read gave rows; for the others <see cref="BuildTactKeyTable.Table"/>
/// says why (its state and reason).
/// </summary>
public enum BuildTactKeyTableOutcome
{
    /// <summary>Every readable row was paged (all of them for Ready, the readable sections for Partial).</summary>
    Read,
    /// <summary>The table isn't Ready or Partial: missing, not in the build, encrypted, no definition and the like.</summary>
    NotReadable,
    /// <summary>Not read because the other table isn't readable: a key without its name, or a name without its key, is no use.</summary>
    NotNeeded,
    /// <summary>The header lacks the ID-joined byte columns (<c>TACTID[0..7]</c> or <c>Key[0..15]</c>).</summary>
    MissingColumns,
    /// <summary>The table's identity changed twice while it was paged.</summary>
    Stale,
    /// <summary>More than <see cref="BuildTactKeys.MaxRows"/> rows.</summary>
    TooManyRows,
}

/// <summary>One source table: its checked entry and how reading it went.</summary>
public sealed record BuildTactKeyTable(TableEntry Table, BuildTactKeyTableOutcome Outcome);

/// <summary>
/// The TACT keys one build's own TactKey and TactKeyLookup tables give (D260), as an immutable snapshot. It is the build's
/// <see cref="ITactKeys"/> for layer 4 (<c>FileSystemOptions.Keys</c>) and its <see cref="ITactKeyStatus"/> for the generated store:
/// a key's fingerprint is the lower-hex SHA-256 of its 16 bytes. Use it for that build only.
/// </summary>
public sealed class BuildTactKeySet : ITactKeys, ITactKeyStatus
{
    private readonly Dictionary<ulong, byte[]> keys;
    private readonly Dictionary<string, string> fingerprints;

    internal BuildTactKeySet(Dictionary<ulong, byte[]> keys, BuildTactKeyTable keyTable, BuildTactKeyTable lookupTable,
        int skippedZero, int unmatched, int unknown, IReadOnlyList<string> conflicts, int malformed)
    {
        this.keys = keys;
        fingerprints = new(StringComparer.Ordinal);
        foreach (var (name, key) in keys) fingerprints[BuildTactKeys.NameOf(name)] = Convert.ToHexStringLower(SHA256.HashData(key));
        Names = new ReadOnlyCollection<string>([.. fingerprints.Keys.Order(StringComparer.Ordinal)]);
        KeyTable = keyTable;
        LookupTable = lookupTable;
        SkippedZero = skippedZero;
        Unmatched = unmatched;
        Unknown = unknown;
        Conflicts = conflicts;
        Malformed = malformed;
    }

    /// <summary>The key names (16 upper hex), sorted.</summary>
    public IReadOnlyList<string> Names { get; }
    public int Count => keys.Count;

    /// <summary>The TactKey table (FileDataID 1302850).</summary>
    public BuildTactKeyTable KeyTable { get; }
    /// <summary>The TactKeyLookup table (FileDataID 1302851).</summary>
    public BuildTactKeyTable LookupTable { get; }
    /// <summary>All-zero keys: placeholders, never used.</summary>
    public int SkippedZero { get; }
    /// <summary>Rows on either side with no row of the same ID on the other, which is complete (Ready).</summary>
    public int Unmatched { get; }
    /// <summary>Rows on either side with no row of the same ID on the other, which is Partial: the row may sit in a keyed section.</summary>
    public int Unknown { get; }
    /// <summary>Names joined to two different keys, sorted; neither key is used.</summary>
    public IReadOnlyList<string> Conflicts { get; }
    /// <summary>
    /// Rows with a byte cell that isn't an exact decimal 0 to 255 (or -128 to -1 for a column the definition declares signed 8-bit,
    /// read as two's complement), or an ID given twice with different bytes. The row on the other side of a malformed one counts
    /// only here, not as unmatched.
    /// </summary>
    public int Malformed { get; }

    public ITactKeys Keys => this;
    public ITactKeyStatus Status => this;

    public ReadOnlyMemory<byte>? Find(ulong keyName) => keys.TryGetValue(keyName, out var key) ? key : (ReadOnlyMemory<byte>?)null;

    public string? FingerprintOf(string keyName) => fingerprints.GetValueOrDefault(keyName);
}

/// <summary>
/// Reads a build's TACT keys from its own TactKeyLookup (ID, TACTID u8[8]) and TactKey (ID, Key u8[16]) tables through
/// <see cref="IBuildTables"/> paging only (D260). Joined by ID; the name is the TACTID bytes read as a little-endian u64. No
/// third-party key lists and no defaults. Table problems never throw: an unreadable table gives an empty set and its entry
/// says why. Throws only for a null handle, cancellation and a disposed handle.
/// </summary>
public static class BuildTactKeys
{
    public const uint TactKeyFileDataId = 1302850;
    public const uint TactKeyLookupFileDataId = 1302851;
    public const string TactKeyName = "TactKey";
    public const string TactKeyLookupName = "TactKeyLookup";
    /// <summary>The most rows read from one table; real builds have a few hundred.</summary>
    public const int MaxRows = 65536;
    /// <summary>Rows asked for per page, within <see cref="TableQueries.MaxLimit"/>.</summary>
    public const int PageRows = 500;

    private const int NameBytes = 8;
    private const int KeyBytes = 16;

    public static async Task<BuildTactKeySet> LoadAsync(IBuildTables tables, TablePriority priority, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tables);
        if (!Enum.IsDefined(priority)) throw new ArgumentOutOfRangeException(nameof(priority));
        cancellationToken.ThrowIfCancellationRequested();

        var keySchema = await SchemaAsync(tables, TactKeyFileDataId, TactKeyName, priority, cancellationToken).ConfigureAwait(false);
        var lookupSchema = await SchemaAsync(tables, TactKeyLookupFileDataId, TactKeyLookupName, priority, cancellationToken).ConfigureAwait(false);
        var bothReadable = TableQueries.IsReadable(keySchema.Table.State) && TableQueries.IsReadable(lookupSchema.Table.State);

        var keyRead = await ReadAsync(tables, keySchema, "Key", KeyBytes, bothReadable, priority, cancellationToken).ConfigureAwait(false);
        var lookupRead = await ReadAsync(tables, lookupSchema, "TACTID", NameBytes, bothReadable, priority, cancellationToken).ConfigureAwait(false);

        var malformed = keyRead.Malformed + lookupRead.Malformed;
        if (keyRead.Table.Outcome != BuildTactKeyTableOutcome.Read || lookupRead.Table.Outcome != BuildTactKeyTableOutcome.Read)
            return new([], keyRead.Table, lookupRead.Table, 0, 0, 0, [], malformed);

        var skippedZero = 0;
        var keyById = new Dictionary<uint, byte[]>();
        foreach (var (id, key) in keyRead.Rows)
        {
            if (key.AsSpan().IndexOfAnyExcept((byte)0) < 0) skippedZero++;
            else keyById[id] = key;
        }

        var unmatched = 0;
        var unknown = 0;
        // A row with no partner: skipped quietly when the partner was a placeholder or malformed, unknown when the other table is Partial.
        void Unpaired(Read other, uint id)
        {
            if (other.Rows.ContainsKey(id) || other.MalformedIds.Contains(id)) return;
            if (other.Table.Table.State == TableState.Partial) unknown++;
            else unmatched++;
        }
        var joined = new Dictionary<ulong, byte[]>();
        var conflicted = new HashSet<ulong>();
        foreach (var (id, tactId) in lookupRead.Rows)
        {
            if (!keyById.Remove(id, out var key))
            {
                Unpaired(keyRead, id);
                continue;
            }
            var name = BinaryPrimitives.ReadUInt64LittleEndian(tactId);
            if (conflicted.Contains(name)) continue;
            if (joined.TryGetValue(name, out var existing))
            {
                if (existing.AsSpan().SequenceEqual(key)) continue;
                joined.Remove(name);
                conflicted.Add(name);
                continue;
            }
            joined[name] = key;
        }
        foreach (var id in keyById.Keys) Unpaired(lookupRead, id);
        var conflicts = conflicted.Select(NameOf).Order(StringComparer.Ordinal).ToArray();
        return new(joined, keyRead.Table, lookupRead.Table, skippedZero, unmatched, unknown, conflicts, malformed);
    }

    /// <summary>A key name as layer 4 and the DB2 reader spell it: 16 upper hex.</summary>
    public static string NameOf(ulong keyName) => keyName.ToString("X16", CultureInfo.InvariantCulture);

    // By FileDataID first; by name when the FileDataID isn't readable and the name finds a readable table under another one.
    private static async Task<TableSchemaResult> SchemaAsync(IBuildTables tables, uint fileDataId, string name, TablePriority priority,
        CancellationToken cancellationToken)
    {
        var byId = await tables.SchemaAsync(TableRef.Id(fileDataId), priority, cancellationToken).ConfigureAwait(false);
        if (TableQueries.IsReadable(byId.Table.State)) return byId;
        var byName = await tables.SchemaAsync(TableRef.ForName(name), priority, cancellationToken).ConfigureAwait(false);
        return TableQueries.IsReadable(byName.Table.State) && byName.Table.FileDataId != fileDataId ? byName : byId;
    }

    private sealed record Read(BuildTactKeyTable Table, Dictionary<uint, byte[]> Rows, HashSet<uint> MalformedIds, int Malformed);

    private static Read Empty(TableEntry entry, BuildTactKeyTableOutcome outcome) => new(new(entry, outcome), [], [], 0);

    private static async Task<Read> ReadAsync(IBuildTables tables, TableSchemaResult schema, string column, int width, bool needed,
        TablePriority priority, CancellationToken cancellationToken)
    {
        var entry = schema.Table;
        if (!TableQueries.IsReadable(entry.State)) return Empty(entry, BuildTactKeyTableOutcome.NotReadable);
        if (!needed) return Empty(entry, BuildTactKeyTableOutcome.NotNeeded);
        var indexes = Columns(schema.Columns, column, width);
        if (indexes is null) return Empty(entry, BuildTactKeyTableOutcome.MissingColumns);
        var signed = schema.Schema?.Columns.Any(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase)
            && c.Kind == ColumnKind.Int && c.Bits == 8 && c.Unsigned == false) == true;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var rows = new Dictionary<uint, byte[]>();
            var bad = new HashSet<uint>();
            var malformed = 0;
            var total = 0L;
            long after = -1;
            string? identity = null;
            var stale = false;
            while (true)
            {
                var page = await tables.PageAsync(new TablePageRequest(TableRef.Id(entry.FileDataId), after, Math.Min(PageRows, TableQueries.MaxLimit), null, identity),
                    priority, cancellationToken).ConfigureAwait(false);
                if (page.State == TablePageState.Stale) { stale = true; break; }
                if (page.State != TablePageState.Ok)
                    return Empty(page.Table, BuildTactKeyTableOutcome.NotReadable);
                entry = page.Table;
                identity = page.Identity;
                foreach (var row in page.Rows)
                {
                    if (++total > MaxRows) return Empty(entry, BuildTactKeyTableOutcome.TooManyRows);
                    var id = row.Row.Id;
                    if (bad.Contains(id)) { malformed++; continue; }
                    var bytes = Bytes(row.Row.Values, indexes, signed);
                    if (bytes is null) { malformed++; bad.Add(id); rows.Remove(id); continue; }
                    if (rows.TryGetValue(id, out var existing))
                    {
                        if (existing.AsSpan().SequenceEqual(bytes)) continue;
                        // Two rows with one ID and different bytes: neither can be trusted.
                        rows.Remove(id);
                        bad.Add(id);
                        malformed += 2;
                        continue;
                    }
                    rows[id] = bytes;
                }
                if (page.NextAfter is not { } next) break;
                after = next;
            }
            if (!stale) return new(new(entry, BuildTactKeyTableOutcome.Read), rows, bad, malformed);
        }
        return Empty(entry, BuildTactKeyTableOutcome.Stale);
    }

    private static int[]? Columns(IReadOnlyList<Db2Column>? columns, string name, int width)
    {
        if (columns is null) return null;
        var indexes = new int[width];
        for (var i = 0; i < width; i++)
        {
            var wanted = $"{name}[{i}]";
            var index = -1;
            for (var c = 0; c < columns.Count; c++)
                if (string.Equals(columns[c].Name, wanted, StringComparison.OrdinalIgnoreCase)) { index = c; break; }
            if (index < 0) return null;
            indexes[i] = index;
        }
        return indexes;
    }

    private static byte[]? Bytes(IReadOnlyList<string> values, int[] indexes, bool signed)
    {
        var bytes = new byte[indexes.Length];
        for (var i = 0; i < indexes.Length; i++)
        {
            if (indexes[i] >= values.Count || ByteOf(values[indexes[i]], signed) is not { } value) return null;
            bytes[i] = value;
        }
        return bytes;
    }

    // An exact decimal 0 to 255: digits only, no sign, no leading zero. A signed int8 column also gives -128 to -1, as two's complement.
    private static byte? ByteOf(string? text, bool signed)
    {
        if (signed && text is { Length: >= 2 } && text[0] == '-' && text[1] != '0')
            return ByteOf(text[1..], false) is { } magnitude && magnitude <= 128 ? (byte)(256 - magnitude) : null;
        if (text is not { Length: >= 1 and <= 3 } || !text.All(char.IsAsciiDigit) || (text.Length > 1 && text[0] == '0')) return null;
        var value = int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
        return value <= 255 ? (byte)value : null;
    }
}
