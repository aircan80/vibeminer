using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace VibeMiner.Objects.Testing;

/// <summary>
/// The large synthetic store the scale test and the lookups benchmark share (#153). It is built once per size in the main
/// checkout's gitignored <c>.local/scale-store/&lt;N&gt;M/</c> (found through <c>git rev-parse --git-common-dir</c>, so every
/// worktree reuses it) and reused while its recorded format and object count match. Open it read-only; anything that writes
/// works on <see cref="CopyAsync"/>'s reflink copy, never on the cache.
/// </summary>
public static class ScaleStore
{
    /// <summary>Bumped when the store's on-disk format or the synthetic objects change, so an old cache is rebuilt.</summary>
    public const int Format = 3;  // 3: the store row's clean-close marker
    private const string Manifest = "scale-store.json";

    private sealed record Record(int Format, long Objects, DateTimeOffset BuiltAt, double BuildSeconds);

    /// <summary>17-byte headerless BLTE objects, one per index i: the same bytes and keys on every machine.</summary>
    public static PutObject Tiny(long i)
    {
        var b = new byte[17]; "BLTE"u8.CopyTo(b); b[8] = (byte)'N'; BitConverter.TryWriteBytes(b.AsSpan(9), i);
        return new(new ObjectKey(ObjectKeyType.Encoded, MD5.HashData(b)), b.Length, b);
    }

    /// <summary>The main checkout's <c>.local/scale-store</c>, for any worktree of it; this tree's own <c>.local</c> outside git.</summary>
    public static string CacheRoot(string repositoryRoot)
    {
        try
        {
            var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = repositoryRoot };
            foreach (var a in new[] { "rev-parse", "--git-common-dir" }) start.ArgumentList.Add(a);
            using var git = Process.Start(start)!;
            var common = git.StandardOutput.ReadToEnd().Trim();
            git.WaitForExit();
            if (git.ExitCode == 0 && common.Length > 0)
            {
                var full = Path.IsPathRooted(common) ? common : Path.GetFullPath(Path.Combine(repositoryRoot, common));
                return Path.Combine(Path.GetDirectoryName(full.TrimEnd(Path.DirectorySeparatorChar))!, ".local", "scale-store");
            }
        }
        catch (System.ComponentModel.Win32Exception) { /* no git: fall back below */ }
        return Path.Combine(repositoryRoot, ".local", "scale-store");
    }

    /// <summary>
    /// The store folder holding <paramref name="millions"/> million objects, built now only when missing, of another format or
    /// count, or when <paramref name="rebuild"/> asks. Builds go to a private folder and are moved into place when complete, so a
    /// run that stops half way never leaves a cache that looks finished.
    /// </summary>
    public static async Task<string> EnsureAsync(string repositoryRoot, int millions, bool rebuild, Action<string> log)
    {
        var root = Path.Combine(CacheRoot(repositoryRoot), $"{millions}M");
        var store = Path.Combine(root, ObjectStore.DefaultFolderName);
        var total = millions * 1_000_000L;
        if (!rebuild && Read(root) is { } r && r.Format == Format && r.Objects == total && Directory.Exists(store))
        {
            log($"reusing the {millions}M scale store at {root} (built {r.BuiltAt:u} in {r.BuildSeconds:F0} s)");
            return store;
        }
        var building = $"{root}.building-{Environment.ProcessId}";
        if (Directory.Exists(building)) Directory.Delete(building, true);
        var buildStore = Path.Combine(building, ObjectStore.DefaultFolderName);
        log($"building the {millions}M scale store in {building}");
        var sw = Stopwatch.StartNew();
        var options = new ObjectStoreOptions { CommitObjects = 32_768, CommitBytes = 256L << 20, CommitDelay = TimeSpan.FromSeconds(5) };
        await using (var s = await ObjectStore.OpenAsync(buildStore, options))
        {
            long next = -1;
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            {
                while (true)
                {
                    var start = Interlocked.Add(ref next, 4096) - 4095;
                    if (start >= total) return;
                    await s.PutAsync(Enumerable.Range(0, (int)Math.Min(4096, total - start)).Select(i => Tiny(start + i)).ToList());
                }
            })));
        }
        File.WriteAllText(Path.Combine(building, Manifest), JsonSerializer.Serialize(new Record(Format, total, DateTimeOffset.UtcNow, sw.Elapsed.TotalSeconds)));
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(Path.GetDirectoryName(root)!);
        Directory.Move(building, root);
        log($"built {total:N0} objects in {sw.Elapsed.TotalSeconds:F0} s");
        return store;
    }

    /// <summary>A private copy of a cached store to write to: a reflink (instant, shares blocks) where the file system has them, else a plain copy.</summary>
    public static async Task<string> CopyAsync(string store, string target)
    {
        Directory.CreateDirectory(target);
        var copy = Path.Combine(target, ObjectStore.DefaultFolderName);
        if (!OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("cp") { UseShellExecute = false };
            foreach (var a in new[] { "-R", "--reflink=auto", store, copy }) start.ArgumentList.Add(a);
            try
            {
                using var cp = Process.Start(start)!;
                await cp.WaitForExitAsync();
                if (cp.ExitCode == 0) return copy;
            }
            catch (System.ComponentModel.Win32Exception) { /* no cp: copy below */ }
            if (Directory.Exists(copy)) Directory.Delete(copy, true);
        }
        foreach (var file in Directory.EnumerateFiles(store, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(copy, Path.GetRelativePath(store, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }
        return copy;
    }

    private static Record? Read(string root)
    {
        try { return JsonSerializer.Deserialize<Record>(File.ReadAllText(Path.Combine(root, Manifest))); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
}
