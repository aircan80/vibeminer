using VibeMiner.Builds;
using VibeMiner.Cli.Browse;
using VibeMiner.Concepts.Creatures;
using VibeMiner.Concepts.ItemEffects;
using VibeMiner.Concepts.Items;
using VibeMiner.Files;
using VibeMiner.Formats.Images;
using VibeMiner.Formats.Tables;
using VibeMiner.Generated;
using VibeMiner.ObjectAccess;

namespace VibeMiner.Cli;

/// <summary>
/// Layer 6 for one load or one command (#204, D472): items, item effects and creatures over one layer 5 <see cref="ITables"/>, one
/// layer 4 <see cref="IFileSystem"/> and one <see cref="BlpImages"/>. Disposing it runs the close it was given (which may close
/// nothing: browse keeps its composition for the session).
/// </summary>
internal sealed class ConceptsLease(IItems items, IItemEffects effects, ICreatures creatures, Func<ValueTask>? close = null) : IAsyncDisposable
{
    private int disposed;
    public IItems Items { get; } = items;
    public IItemEffects Effects { get; } = effects;
    public ICreatures Creatures { get; } = creatures;

    /// <summary>The real layer 6 services over these layers.</summary>
    public static ConceptsLease Over(ITables tables, IFileSystem files, BlpImages images, Func<ValueTask>? close = null) =>
        new(new ItemsService(tables, files, images), new ItemEffectsService(tables), new CreaturesService(tables, files, images), close);

    public ValueTask DisposeAsync() => Interlocked.Exchange(ref disposed, 1) != 0 || close is null ? ValueTask.CompletedTask : close();
}

/// <summary>The composition <c>vm concepts states</c> uses by default (D472).</summary>
internal static class ConceptsComposition
{
    /// <summary>
    /// Composes layer 6 over an open store handle: one build opener shared by the tables composition and the icon and model file
    /// system, the data folder's generated store opened once for tables, names and images, and the names switch from settings.
    /// The file system and images know no TACT keys (as in browse): an encrypted icon or model is Protected with its key names.
    /// Closing the lease closes the composition, the generated store, then the handle. When composing throws, the caller still
    /// owns the handle. Throws <see cref="GeneratedStoreException"/> when the generated store can't be opened.
    /// </summary>
    public static async Task<ConceptsLease> LeaseAsync(IStoreHandle handle, string dataFolder, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var generated = await GeneratedStore.OpenAsync(TablesComposition.GeneratedFolder(dataFolder), cancellationToken: ct).ConfigureAwait(false);
        try
        {
            var names = new SettingsNameSwitch(dataFolder);
            var builds = new BuildOpener(handle.Objects);
            var tables = await TablesComposition.OpenAsync(handle, generated, names, fetchDefinitions: false, ct, builds).ConfigureAwait(false);
            var files = new FileSystem(builds, handle.Objects, new FileSystemOptions { Names = names, NamesStore = generated });
            return ConceptsLease.Over(tables, files, new BlpImages(generated), async () =>
            {
                try
                {
                    try { await tables.DisposeAsync().ConfigureAwait(false); }
                    finally { await generated.DisposeAsync().ConfigureAwait(false); }
                }
                finally { await handle.DisposeAsync().ConfigureAwait(false); }
            });
        }
        catch
        {
            await generated.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
