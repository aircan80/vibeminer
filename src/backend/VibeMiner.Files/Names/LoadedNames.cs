using System.Runtime.CompilerServices;
using VibeMiner.Generated;

namespace VibeMiner.Files.Names;

/// <summary>
/// The loaded copy's names as Files pages read them: <see cref="ListfileStore"/> lookups that never throw a generated store
/// failure (#186). A read that fails answers as no name for that call and marks this copy damaged, so the next load of
/// <see cref="FileNames"/> rebuilds it from the object store. Cancellation still throws. Thread-safe.
/// </summary>
internal sealed class LoadedNames
{
    private readonly ListfileStore index;
    private readonly Action<LoadedNames> damage;
    private volatile bool damaged;

    internal LoadedNames(ListfileStore index, Action<LoadedNames> damage)
    {
        this.index = index; this.damage = damage;
    }

    public string Tag => index.Tag;
    public string Sha256 => index.Sha256;
    public long Count => index.Count;
    /// <summary>The index's manifests and segments in the generated store (<see cref="ListfileStore.Keys"/>).</summary>
    public IReadOnlyList<GeneratedKey> Keys => index.Keys;

    /// <summary>Whether a read of this copy failed; its answers after that may be missing names.</summary>
    public bool IsDamaged => damaged;

    /// <summary>The name of a FileDataID, or null when the listfile doesn't name it or the read failed.</summary>
    public async ValueTask<string?> NameOfAsync(uint fileDataId, CancellationToken cancellationToken)
    {
        try { return await index.NameOfAsync(fileDataId, cancellationToken).ConfigureAwait(false); }
        catch (GeneratedStoreException) { Damage(); return null; }
    }

    /// <summary>Every FileDataID named <paramref name="path"/>, ascending; empty when none or the read failed.</summary>
    public async ValueTask<IReadOnlyList<uint>> IdsOfAsync(string path, CancellationToken cancellationToken)
    {
        try { return await index.IdsOfAsync(path, cancellationToken).ConfigureAwait(false); }
        catch (GeneratedStoreException) { Damage(); return []; }
    }

    /// <summary>Every named FileDataID from <paramref name="fromId"/> up, ascending; a failed read ends the walk early.</summary>
    public async IAsyncEnumerable<(uint Id, string Name)> FromAsync(uint fromId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var names = index.FromAsync(fromId, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                var more = false;
                try { more = await names.MoveNextAsync().ConfigureAwait(false); }
                catch (GeneratedStoreException) { Damage(); }
                if (!more) yield break;
                yield return names.Current;
            }
        }
        finally { await names.DisposeAsync().ConfigureAwait(false); }
    }

    private void Damage()
    {
        damaged = true;
        damage(this);
    }
}
