using VibeMiner.Builds;
using VibeMiner.Formats;
using VibeMiner.Formats.Tables;

namespace VibeMiner.Concepts.Tests.Tables;

/// <summary>An <see cref="IBuildTables"/> that runs <c>after</c> once each page is read (honouring the page's token).</summary>
internal sealed class HookedTables(IBuildTables inner, Func<TablePageRequest, TablePriority, Task> after) : IBuildTables
{
    public HookedTables(IBuildTables inner, Func<TablePageRequest, Task> after) : this(inner, (request, _) => after(request)) { }

    public BuildRef Build => inner.Build;
    public string Locale => inner.Locale;
    public ValueTask DisposeAsync() => inner.DisposeAsync();
    public IAsyncEnumerable<TableEntry> ListAsync(TableListFilter filter, TablePriority priority, CancellationToken cancellationToken) =>
        inner.ListAsync(filter, priority, cancellationToken);
    public Task<TableEntry> CheckAsync(TableRef table, TablePriority priority, CancellationToken cancellationToken) => inner.CheckAsync(table, priority, cancellationToken);
    public Task<TableSchemaResult> SchemaAsync(TableRef table, TablePriority priority, CancellationToken cancellationToken) =>
        inner.SchemaAsync(table, priority, cancellationToken);
    public Task<TableRowResult> GetAsync(TableRef table, uint id, TablePriority priority, CancellationToken cancellationToken) =>
        inner.GetAsync(table, id, priority, cancellationToken);
    public Task<TableFollowResult> FollowAsync(TableFollowRequest request, TablePriority priority, CancellationToken cancellationToken) =>
        inner.FollowAsync(request, priority, cancellationToken);
    public Task<TableReferencesResult> ReferencedByAsync(TableReferencesRequest request, TablePriority priority, CancellationToken cancellationToken) =>
        inner.ReferencedByAsync(request, priority, cancellationToken);
    public Task<TablePreloadResult> PreloadAsync(IProgress<TablePreloadProgress>? progress, CancellationToken cancellationToken) =>
        inner.PreloadAsync(progress, cancellationToken);

    public async Task<TablePage> PageAsync(TablePageRequest request, TablePriority priority, CancellationToken cancellationToken)
    {
        var page = await inner.PageAsync(request, priority, cancellationToken);
        await after(request, priority).WaitAsync(cancellationToken);
        return page;
    }
}

/// <summary>An <see cref="ITables"/> whose handles are <see cref="HookedTables"/>.</summary>
internal sealed class HookedTablesService(ITables inner, Func<TablePageRequest, Task> after) : ITables
{
    public async Task<TablesOpenResult> ForAsync(BuildRef build, string locale, CancellationToken cancellationToken)
    {
        var opened = await inner.ForAsync(build, locale, cancellationToken);
        return opened.Tables is null ? opened : opened with { Tables = new HookedTables(opened.Tables, after) };
    }
}
