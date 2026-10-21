using System.Reflection;

namespace VibeMiner.Backend.Tests;

// Authored repository files (contracts/, tests/desktop/fixtures/) are read from the source tree the
// test assembly was built from, not from wherever its build output landed. The csproj embeds that
// root as assembly metadata; VIBEMINER_REPO_ROOT is the fallback when the binaries outlive it.
internal static class RepositoryPaths
{
    internal const string MetadataKey = "VibeMinerRepositoryRoot";
    internal const string EnvironmentVariable = "VIBEMINER_REPO_ROOT";
    internal const string Marker = "contracts/discovery.schema.json";

    private static readonly Lazy<string> RootValue = new(() => Resolve(
        typeof(RepositoryPaths).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == MetadataKey)?.Value,
        Environment.GetEnvironmentVariable(EnvironmentVariable)));

    internal static string Root => RootValue.Value;

    internal static string File(string relative) => Path.Combine(Root, relative);

    internal static string Resolve(string? embedded, string? environment)
    {
        foreach (var candidate in new[] { embedded, environment })
            if (!string.IsNullOrWhiteSpace(candidate) && Path.IsPathFullyQualified(candidate) && System.IO.File.Exists(Path.Combine(candidate, Marker)))
                return Path.GetFullPath(candidate);
        throw new InvalidOperationException(
            $"Repository root not found: neither the embedded {MetadataKey} ('{embedded}') nor {EnvironmentVariable} ('{environment}') contains {Marker}.");
    }
}
