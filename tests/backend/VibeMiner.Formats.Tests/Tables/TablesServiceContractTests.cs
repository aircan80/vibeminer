using VibeMiner.Formats.Tables;

namespace VibeMiner.Formats.Tests.Tables;

/// <summary>The <see cref="TablesContract"/> against the real <see cref="TablesService"/> over synthetic WDC5 files and definitions.</summary>
public sealed class TablesServiceContractTests : TablesContract
{
    protected override async Task<ITables> CreateAsync(TableScenario scenario)
    {
        var world = await SyntheticTables.CreateAsync(scenario.Build, [.. scenario.Tables.Select(SyntheticTables.From)], maxScanRows: scenario.MaxScanRows);
        // A generated table was checked before: by an earlier handle, through the store.
        await using (var tables = (await world.Service.ForAsync(scenario.Build, scenario.Locale, default)).Tables!)
            foreach (var table in scenario.Tables.Where(t => t.Generated)) await tables.CheckAsync(table.FileDataId, TablePriority.Interactive, default);
        return world.Service;
    }
}
