using System.Reflection;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class RepositoryPathsTests
{
    [Fact]
    public void Build_embeds_the_source_root_rather_than_relying_on_the_output_location()
    {
        var embedded = typeof(RepositoryPaths).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == RepositoryPaths.MetadataKey).Value;
        Assert.NotNull(embedded);
        Assert.True(Path.IsPathFullyQualified(embedded));
        // The tree may have moved since the build, with VIBEMINER_REPO_ROOT pointing at its new place.
        Assert.Equal(RepositoryPaths.Resolve(embedded, Environment.GetEnvironmentVariable(RepositoryPaths.EnvironmentVariable)), RepositoryPaths.Root);
        if (File.Exists(Path.Combine(embedded, RepositoryPaths.Marker))) Assert.Equal(Path.GetFullPath(embedded), RepositoryPaths.Root);
        Assert.True(File.Exists(RepositoryPaths.File(RepositoryPaths.Marker)));
        Assert.True(File.Exists(RepositoryPaths.File("tests/desktop/fixtures/items-snapshot.json")));
    }

    [Fact]
    public void Embedded_root_wins_and_the_environment_is_only_a_fallback()
    {
        using var embedded = new Tree(true);
        using var environment = new Tree(true);
        using var stale = new Tree(false);
        Assert.Equal(embedded.Path, RepositoryPaths.Resolve(embedded.Path, environment.Path));
        Assert.Equal(environment.Path, RepositoryPaths.Resolve(stale.Path, environment.Path));
        Assert.Equal(environment.Path, RepositoryPaths.Resolve(null, environment.Path));
        Assert.Equal(environment.Path, RepositoryPaths.Resolve(System.IO.Path.Combine(stale.Path, "removed"), environment.Path));
    }

    [Fact]
    public void Missing_relative_or_unmarked_roots_fail_naming_both_sources()
    {
        using var stale = new Tree(false);
        var error = Assert.Throws<InvalidOperationException>(() => RepositoryPaths.Resolve(stale.Path, "contracts/.."));
        Assert.Contains(RepositoryPaths.MetadataKey, error.Message, StringComparison.Ordinal);
        Assert.Contains(RepositoryPaths.EnvironmentVariable, error.Message, StringComparison.Ordinal);
        Assert.Contains(stale.Path, error.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => RepositoryPaths.Resolve(null, null));
        Assert.Throws<InvalidOperationException>(() => RepositoryPaths.Resolve(" ", ""));
    }

    private sealed class Tree : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vibeminer-repo-root-" + Guid.NewGuid().ToString("N"));

        internal Tree(bool marked)
        {
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "contracts"));
            if (marked) File.WriteAllText(System.IO.Path.Combine(Path, RepositoryPaths.Marker), "{}");
        }

        public void Dispose() => Directory.Delete(Path, true);
    }
}
