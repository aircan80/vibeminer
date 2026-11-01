using System.Text.Json;

namespace VibeMiner.Cli;

/// <summary>What the resolver sees: the OS ("linux", "darwin" or "win32", as Node names them), the environment and the home folder.</summary>
internal sealed record CliEnvironment(string Platform, Func<string, string?> Variable, string Home)
{
    public static CliEnvironment Current() => new(
        OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "linux",
        Environment.GetEnvironmentVariable,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
}

internal enum DataFolderSource { Flag, Override, Chosen, Default }

internal sealed record DataFolder(string Path, DataFolderSource Source);

/// <summary>A choice file that can't be used: never guessed past (D128). <see cref="File"/> is the file at fault.</summary>
internal sealed record DataFolderProblem(string Problem, string File, string Remedy);

/// <summary>
/// Finds the data folder the way the desktop app does (apps/desktop/src/main/data-folder.ts, D128): <c>--data</c>, then
/// <c>VIBEMINER_DATA_DIR</c>, then the folder chosen in Settings (<c>data-folder-v1.json</c> in Electron's userData folder), then
/// the per-OS default. It only reads: it never creates the folder or writes the choice.
/// </summary>
internal static class DataFolders
{
    public const string FolderName = "VibeMiner";
    public const string ChoiceFile = "data-folder-v1.json";
    /// <summary>Electron's app name, from apps/desktop/package.json; userData is the OS's app-data folder plus this.</summary>
    private static readonly string[] ElectronAppName = ["@vibeminer", "desktop"];
    private const int MaxChoiceBytes = 64 * 1024, MaxPath = 4096;

    public static async Task<object> ResolveAsync(CliEnvironment env, string? flag, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(flag)) return new DataFolder(System.IO.Path.GetFullPath(flag), DataFolderSource.Flag);
        if (env.Variable("VIBEMINER_DATA_DIR") is { Length: > 0 } overrideDir) return new DataFolder(System.IO.Path.GetFullPath(overrideDir), DataFolderSource.Override);
        var choice = await ReadChoiceAsync(UserData(env), env.Platform, ct).ConfigureAwait(false);
        if (choice is DataFolderProblem) return choice;
        if (choice is string chosen) return new DataFolder(chosen, DataFolderSource.Chosen);
        return new DataFolder(Default(env), DataFolderSource.Default);
    }

    public static string Default(CliEnvironment env)
    {
        if (env.Platform == "win32")
        {
            var local = env.Variable("LOCALAPPDATA");
            return Join(env.Platform, local is { Length: > 0 } && IsAbsolute(env.Platform, local) ? local : Join(env.Platform, env.Home, "AppData", "Local"), FolderName);
        }
        if (env.Platform == "darwin") return Join(env.Platform, env.Home, "Library", "Application Support", FolderName);
        // XDG: a relative XDG_DATA_HOME is invalid and is ignored.
        var xdg = env.Variable("XDG_DATA_HOME");
        return Join(env.Platform, xdg is { Length: > 0 } && IsAbsolute(env.Platform, xdg) ? xdg : Join(env.Platform, env.Home, ".local", "share"), FolderName);
    }

    /// <summary>Electron's <c>app.getPath('userData')</c>: %APPDATA%, ~/Library/Application Support or $XDG_CONFIG_HOME (~/.config), plus the app name.</summary>
    public static string UserData(CliEnvironment env)
    {
        string appData;
        if (env.Platform == "win32")
        {
            var roaming = env.Variable("APPDATA");
            appData = roaming is { Length: > 0 } && IsAbsolute(env.Platform, roaming) ? roaming : Join(env.Platform, env.Home, "AppData", "Roaming");
        }
        else if (env.Platform == "darwin") appData = Join(env.Platform, env.Home, "Library", "Application Support");
        else
        {
            var xdg = env.Variable("XDG_CONFIG_HOME");
            appData = xdg is { Length: > 0 } && IsAbsolute(env.Platform, xdg) ? xdg : Join(env.Platform, env.Home, ".config");
        }
        return Join(env.Platform, [appData, .. ElectronAppName]);
    }

    /// <summary>The saved choice: null when there is none, the folder, or a problem when the file exists but can't be used.</summary>
    public static async Task<object?> ReadChoiceAsync(string userData, string platform, CancellationToken ct)
    {
        var file = Join(platform, userData, ChoiceFile);
        DataFolderProblem Bad(string problem) => new(problem, file, $"Remove {file} to use the default folder, or pass --data <dir>.");
        byte[] bytes;
        try
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
            if (stream.Length > MaxChoiceBytes) return Bad("The data folder setting is too large to be one.");
            bytes = new byte[stream.Length];
            await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Bad($"The data folder setting can't be read ({e.GetType().Name})."); }

        JsonElement root;
        try { using var document = JsonDocument.Parse(bytes); root = document.RootElement.Clone(); }
        catch (JsonException) { return Bad("The data folder setting is not valid JSON."); }
        if (root.ValueKind != JsonValueKind.Object) return Bad("The data folder setting is not an object.");
        var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : (double?)null;
        if (version > 1) return Bad("The data folder setting was written by a newer VibeMiner.");
        if (version != 1 || root.EnumerateObject().Any(p => p.Name is not ("version" or "path"))) return Bad("The data folder setting has an unknown shape.");
        if (!root.TryGetProperty("path", out var p) || p.ValueKind != JsonValueKind.String || p.GetString() is not { } path || !ValidFolder(platform, path))
            return Bad("The data folder setting does not name an absolute folder.");
        return Normalize(platform, path);
    }

    private static bool ValidFolder(string platform, string value) =>
        value.Length is > 0 and <= MaxPath && !value.Contains('\0') && IsAbsolute(platform, value);

    private static char Separator(string platform) => platform == "win32" ? '\\' : '/';

    internal static bool IsAbsolute(string platform, string value) => platform == "win32"
        ? value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '\\' or '/' || value.StartsWith(@"\\", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal)
        : value.StartsWith('/');

    internal static string Join(string platform, params string[] parts)
    {
        var separator = Separator(platform);
        var joined = string.Join(separator, parts.Select((part, i) => i == 0 ? part.TrimEnd('/', separator) : part.Trim('/', separator)).Where(part => part.Length > 0));
        return parts.Length > 0 && parts[0].StartsWith(separator) && !joined.StartsWith(separator) ? separator + joined : joined;
    }

    /// <summary>Node's path.normalize for the platform's separators: collapses ".", ".." and repeated separators.</summary>
    internal static string Normalize(string platform, string path)
    {
        var separator = Separator(platform);
        var unified = platform == "win32" ? path.Replace('/', '\\') : path;
        var prefix = platform == "win32" ? (unified.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\" : unified[..3]) : "/";
        var parts = new List<string>();
        foreach (var part in unified[prefix.Length..].Split(separator))
        {
            if (part is "" or ".") continue;
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(part);
        }
        return prefix + string.Join(separator, parts);
    }
}
