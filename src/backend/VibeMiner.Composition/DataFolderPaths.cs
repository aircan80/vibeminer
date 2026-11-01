using VibeMiner.Objects;

namespace VibeMiner.Composition;

/// <summary>Where the layer stores live in a data folder. <c>vm</c> and the desktop Host use the same paths.</summary>
public static class DataFolderPaths
{
    /// <summary>The catalog store, a cache of the history log, created on first use.</summary>
    public const string CatalogFolder = "catalog-v1", CatalogFile = "catalog.sqlite3";

    /// <summary>The object store folder in <paramref name="dataFolder"/>.</summary>
    public static string ObjectStoreFolder(string dataFolder) => Path.Combine(dataFolder, ObjectStore.DefaultFolderName);

    /// <summary>The catalog store file in <paramref name="dataFolder"/>.</summary>
    public static string CatalogPath(string dataFolder) => Path.Combine(dataFolder, CatalogFolder, CatalogFile);
}
