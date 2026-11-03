using System.Text;
using Microsoft.Data.Sqlite;

namespace VibeMiner.Objects;

public enum LibraryOwnerScope { Build = 0, Global = 1 }
public enum LibraryObjectRole { Original = 0, Derived = 1 }

/// <summary>An immutable, borrowed view of one library. Disposing a consumer never closes the shared store.</summary>
public sealed class LibraryObjectOwner : IObjectReader, IObjectWriter, IHistoryLog
{
    private readonly ObjectStore store;
    private readonly LibraryClaim claim;

    internal LibraryObjectOwner(ObjectStore store, LibraryClaim claim) { this.store = store; this.claim = claim; }
    /// <summary>The library folder this immutable view borrows.</summary>
    public string Folder => store.Folder;
    /// <summary>The role captured when the immutable view was created.</summary>
    public LibraryObjectRole Role => claim.Role;

    /// <summary>Expected-object generations for this exact immutable identity; independent of physical membership.</summary>
    public ILibraryInventory Inventory => new LibraryInventory(store, claim);

    public ValueTask<bool> HasAsync(ObjectKey key, CancellationToken ct = default) => store.HasAsync(key, ct);
    /// <summary>Whether this exact owner/scope/producer/role has a durable association with the stored key.</summary>
    public ValueTask<bool> OwnsAsync(ObjectKey key, CancellationToken ct = default) => store.OwnsAsync(claim, key, ct);

    public async ValueTask<ObjectRead> ReadAsync(ObjectKey key, CancellationToken ct = default)
    {
        var read = await store.ReadAsync(key, ct);
        if (read.State == ReadState.Present) await store.ClaimAsync(claim, key, ct);
        return read;
    }

    public async ValueTask<RangeRead> ReadRangeAsync(ObjectKey key, long offset, Memory<byte> destination, CancellationToken ct = default)
    {
        var read = await store.ReadRangeAsync(key, offset, destination, ct);
        if (read.State == ReadState.Present) await store.ClaimAsync(claim, key, ct);
        return read;
    }

    public Task<IReadOnlyList<PutResult>> PutAsync(IReadOnlyList<PutObject> batch, bool commitNow = false, CancellationToken ct = default) =>
        store.PutOwnedAsync(claim, batch, commitNow, ct);
    public Task<IReadOnlyList<PutResult>> AppendHistoryAsync(HistoryObservation observation, CancellationToken ct = default) =>
        PutAsync(HistoryRecordFormat.Objects(observation), commitNow: true, ct);
    public ValueTask<IReadOnlyList<HistoryEntry>> ReadHistoryAsync(HistoryQuery query, CancellationToken ct = default) => store.ReadHistoryAsync(query, ct);

    // Physical removal and owner-bound staging require their own lifecycle contracts; do not expose ownerless fallbacks.
    public Task<IReadOnlyList<DeleteResult>> DeleteAsync(IReadOnlyList<ObjectKey> keys, CancellationToken ct = default) =>
        throw ObjectStore.LibraryUnsupported();
    public Task<IObjectStaging> StageAsync(ObjectKey key, long declaredLength, ObjectOrigin origin = ObjectOrigin.Cdn, CancellationToken ct = default) =>
        throw ObjectStore.LibraryUnsupported();
}

internal sealed record LibraryClaim
{
    public string Owner { get; }
    public LibraryOwnerScope Scope { get; }
    public string Producer { get; }
    public LibraryObjectRole Role { get; }

    public LibraryClaim(string owner, LibraryOwnerScope scope, string producer, LibraryObjectRole role)
    {
        Validate(owner, 512, nameof(owner));
        Validate(producer, 128, nameof(producer));
        if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope));
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        Owner = owner; Scope = scope; Producer = producer; Role = role;
    }

    private static void Validate(string value, int bytes, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0') || Encoding.UTF8.GetByteCount(value) > bytes)
            throw new ArgumentException($"An identity must be nonempty, contain no NUL and fit in {bytes} UTF-8 bytes.", name);
    }
}

internal static class LibraryOwnership
{
    private const string Schema = """
        CREATE TABLE library_format(id INTEGER PRIMARY KEY CHECK(id=1), version INTEGER NOT NULL);
        INSERT INTO library_format VALUES(1,2);
        CREATE TABLE library_membership(owner TEXT NOT NULL, scope INTEGER NOT NULL CHECK(scope IN (0,1)),
            producer TEXT NOT NULL, role INTEGER NOT NULL CHECK(role IN (0,1)), k BLOB NOT NULL CHECK(length(k)=18),
            PRIMARY KEY(owner,scope,producer,role,k)) WITHOUT ROWID;
        CREATE INDEX library_membership_key ON library_membership(k);
        """;

    public static void Create(SqliteConnection c, SqliteTransaction tx)
    {
        ObjectIndex.Exec(c, Schema, tx);
        ObjectIndex.Exec(c, LibraryInventoryMetadata.Schema, tx);
    }

    public static void Validate(string path, bool immutable = false, CancellationToken ct = default)
    {
        try
        {
            using var c = ObjectIndex.Open(path, readOnly: true, immutable);
            using var interrupt = ct.Register(() => SQLitePCL.raw.sqlite3_interrupt(c.Handle));
            using var snapshot = c.BeginTransaction();
            using var version = c.CreateCommand();
            version.Transaction = snapshot;
            version.CommandText = "SELECT version FROM library_format WHERE id=1";
            var found = version.ExecuteScalar();
            using var formatRows = c.CreateCommand(); formatRows.Transaction = snapshot;
            formatRows.CommandText = "SELECT COUNT(*) FROM library_format";
            if ((long)formatRows.ExecuteScalar()! != 1) throw new ArgumentException("Malformed library format metadata.");
            if (found is not long v || v is < 1 or > 2)
                throw new ObjectStoreException(found is long newer && newer > 2 ? ObjectStoreCodes.NewerFormat : ObjectStoreCodes.IndexUnreadable,
                    "The authoritative library metadata format is unsupported or missing; it cannot be rebuilt from packs.");
            if (v == 2) LibraryInventoryMetadata.Validate(c, snapshot, ct);
            else
            {
                // A v1 library may be upgraded only from a clean absence of v2 tables, never from damaged/partially missing metadata.
                using var leftovers = c.CreateCommand(); leftovers.Transaction = snapshot;
                leftovers.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE name IN ('library_inventory_identity','library_inventory_generation','library_inventory_key'))";
                if ((long)leftovers.ExecuteScalar()! != 0) throw new ArgumentException("Unexpected inventory metadata in a v1 library.");
            }
            using var membership = c.CreateCommand();
            membership.Transaction = snapshot;
            using var bounds = c.CreateCommand();
            bounds.Transaction = snapshot;
            bounds.CommandText = """
                SELECT EXISTS(SELECT 1 FROM library_membership WHERE typeof(k)!='blob' OR length(k)!=18 OR typeof(owner)!='text' OR
                    length(CAST(owner AS BLOB)) NOT BETWEEN 1 AND 512 OR instr(owner,char(0))!=0 OR
                    typeof(producer)!='text' OR length(CAST(producer AS BLOB)) NOT BETWEEN 1 AND 128 OR instr(producer,char(0))!=0)
                """;
            if ((long)bounds.ExecuteScalar()! != 0) throw new ArgumentException("Malformed library identities.");
            // Scan without retaining rows: ownership is authoritative even after a clean close.
            membership.CommandText = "SELECT owner,scope,producer,role,k FROM library_membership";
            using (var rows = membership.ExecuteReader())
            {
                while (rows.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    if (rows.GetValue(0) is not string owner || rows.GetValue(1) is not long scope || scope is < 0 or > 1 ||
                        rows.GetValue(2) is not string producer || rows.GetValue(3) is not long role || role is < 0 or > 1 ||
                        rows.GetValue(4) is not byte[] { Length: 18 } key || key[1] != 0 || !ObjectFormats.Known(ObjectStoreKind.Library, (ObjectKeyType)key[0]))
                        throw new ArgumentException("Malformed library membership.");
                    _ = new LibraryClaim(owner, (LibraryOwnerScope)scope, producer, (LibraryObjectRole)role);
                }
            }
            using var coverage = c.CreateCommand();
            coverage.Transaction = snapshot;
            coverage.CommandText = """
                SELECT EXISTS(SELECT 1 FROM library_membership m WHERE NOT EXISTS(SELECT 1 FROM objects o WHERE o.k=m.k))
                    OR EXISTS(SELECT 1 FROM objects o WHERE NOT EXISTS(SELECT 1 FROM library_membership m INDEXED BY library_membership_key WHERE m.k=o.k))
                """;
            if ((long)coverage.ExecuteScalar()! != 0) throw new ArgumentException("Library ownership and payload locations disagree.");
        }
        catch (SqliteException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (Exception e) when (e is SqliteException or ArgumentException)
        {
            throw new ObjectStoreException(ObjectStoreCodes.IndexUnreadable, "The authoritative library metadata cannot be read; preserve the library for repair.", e);
        }
    }

    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction? tx, LibraryClaim claim, ObjectKey key, string sql)
    {
        var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$o", claim.Owner); cmd.Parameters.AddWithValue("$s", (int)claim.Scope);
        cmd.Parameters.AddWithValue("$p", claim.Producer); cmd.Parameters.AddWithValue("$r", (int)claim.Role);
        cmd.Parameters.AddWithValue("$k", key.ToIndexKey());
        return cmd;
    }

    public static bool Contains(SqliteConnection c, LibraryClaim claim, ObjectKey key)
    {
        using var cmd = Command(c, null, claim, key,
            "SELECT EXISTS(SELECT 1 FROM library_membership m JOIN objects o ON o.k=m.k WHERE owner=$o AND scope=$s AND producer=$p AND role=$r AND m.k=$k)");
        return (long)cmd.ExecuteScalar()! != 0;
    }

    public static void Attach(SqliteConnection c, SqliteTransaction tx, LibraryClaim claim, ObjectKey key)
    {
        using var cmd = Command(c, tx, claim, key,
            "INSERT OR IGNORE INTO library_membership(owner,scope,producer,role,k) SELECT $o,$s,$p,$r,k FROM objects WHERE k=$k");
        cmd.ExecuteNonQuery();
    }
}

public sealed partial class ObjectStore
{
    /// <summary>Explicitly opens the mixed original/generated library, without changing legacy defaults or profiles.</summary>
    public static Task<ObjectStore> OpenLibraryAsync(string directory, ObjectStoreOptions? options = null, CancellationToken ct = default) =>
        OpenAsync(directory, (options ?? new()) with { Kind = ObjectStoreKind.Library }, ct);

    public LibraryObjectOwner ForOwner(string owner, LibraryOwnerScope scope, string producer, LibraryObjectRole role)
    {
        EnsureWriter();
        if (Kind != ObjectStoreKind.Library) throw new InvalidOperationException("Owner views require a mixed library.");
        return new(this, new(owner, scope, producer, role));
    }

    internal static ObjectStoreException LibraryUnsupported() => new(ObjectStoreCodes.LibraryOperationUnsupported,
        "This operation is not supported for an authoritative library; its owner metadata must be preserved.");

    internal ValueTask<bool> OwnsAsync(LibraryClaim claim, ObjectKey key, CancellationToken ct)
    {
        ThrowIfClosed();
        return readers.Use((claim, key), static (c, _, state) => LibraryOwnership.Contains(c, state.claim, state.key), ct);
    }

    internal async Task ClaimAsync(LibraryClaim claim, ObjectKey key, CancellationToken ct)
    {
        EnsureWriter();
        if (await OwnsAsync(claim, key, ct)) return;
        await MaintainAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            Durably(tx => LibraryOwnership.Attach(wconn!, tx, claim, key));
            return Task.FromResult(true);
        }, ct);
    }
}
