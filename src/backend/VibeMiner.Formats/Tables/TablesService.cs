using System.Globalization;
using VibeMiner.Builds;
using VibeMiner.Files;
using VibeMiner.Formats.Definitions;
using VibeMiner.Formats.Generated;

namespace VibeMiner.Formats.Tables;

/// <summary>
/// The layer 5 <see cref="ITables"/>: DB2 files read through layer 4 with the definitions <paramref name="definitions"/> picks,
/// their rows kept in the generated store. The table list is the definitions' manifest (its tables with a DB2 FileDataID) plus
/// the DB2 files the manifest doesn't name that layer 4 lists under the listfile's <c>dbfilesclient/</c> names; with the
/// community names off, none of those are found. The service keeps the protected IDs of the tables it opened (bounded, see
/// <see cref="ProtectedIds"/>). Expensive work runs in the slots of <see cref="TablesOptions.Scheduler"/>, its own one when none is given.
/// </summary>
public sealed class TablesService(IFileSystem files, TableDefinitions definitions, IGeneratedTables generated, TablesOptions options) : ITables
{
    private readonly IFileSystem files = files ?? throw new ArgumentNullException(nameof(files));
    private readonly TableDefinitions definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
    private readonly IGeneratedTables generated = generated ?? throw new ArgumentNullException(nameof(generated));
    private readonly TablesOptions options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly Lazy<Manifest> manifest = new(() => Manifest.Of(definitions.Tables));
    private readonly ProtectedIds protectedIds = new();
    private readonly TableWorkScheduler scheduler = options?.Scheduler ?? new TableWorkScheduler();

    public async Task<TablesOpenResult> ForAsync(BuildRef build, string locale, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(build);
        FileLocales.BitOf(locale);
        cancellationToken.ThrowIfCancellationRequested();
        var opened = await files.ForAsync(build, locale, cancellationToken).ConfigureAwait(false);
        if (opened.State != FilesOpenState.Open || opened.Files is not { } handle)
            return new(build, locale, opened.State, null, opened.Code, opened.Message, opened.ObservedAt);
        try
        {
            var version = await options.BuildVersion(build, cancellationToken).ConfigureAwait(false);
            if (!IsVersion(version))
            {
                await handle.DisposeAsync().ConfigureAwait(false);
                return new(build, locale, FilesOpenState.Unsupported, null, "TABLES_BUILD_VERSION_UNKNOWN",
                    "The build's exact version isn't known, so no definition can be picked for its tables.", null);
            }
            var limits = options.Limits with { AllowPartialEncryptedSections = true, AllowRawTextEvidence = true };
            return new(build, locale, FilesOpenState.Open,
                new BuildTables(handle, version!, WowLocale.Parse(locale), manifest.Value, definitions, generated, limits, options.Keys,
                    options.MaxScanRows, options.MaxReferenceSources, options.MaxReferenceScanRows, protectedIds, scheduler), null, null, null);
        }
        catch
        {
            await handle.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static bool IsVersion(string? version)
    {
        if (version is null) return false;
        try { DbdBuildVersion.Parse(version); return true; }
        catch (Db2FormatException) { return false; }
    }

    /// <summary>The manifest's tables by DB2 FileDataID (the first entry wins when two give one) and in name order for lookups.</summary>
    internal sealed class Manifest
    {
        private Manifest(SortedDictionary<uint, DefinitionTable> byId) => ById = byId;

        public SortedDictionary<uint, DefinitionTable> ById { get; }

        public static Manifest Of(IReadOnlyList<DefinitionTable> tables)
        {
            var byId = new SortedDictionary<uint, DefinitionTable>();
            foreach (var table in tables)
                if (table.Db2FileDataId is { } text && uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id != 0)
                    byId.TryAdd(id, table);
            return new(byId);
        }

        /// <summary>The table with this definition name, ignoring ASCII case only.</summary>
        public (uint Id, DefinitionTable Table)? Named(string name)
        {
            foreach (var (id, table) in ById)
                if (table.Name is { } own && TableQueries.NamesEqual(own, name)) return (id, table);
            return null;
        }
    }
}
