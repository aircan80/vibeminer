using Xunit;

namespace VibeMiner.Cli.Tests;

/// <summary>The resolver matches apps/desktop/src/main/data-folder.ts (D128).</summary>
public sealed class DataFolderTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), "vibeminer-cli-test-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(home)) Directory.Delete(home, true); }

    private CliEnvironment Linux(params (string, string)[] variables) =>
        new("linux", name => variables.FirstOrDefault(v => v.Item1 == name).Item2, home);

    private string ChoiceFile => Path.Combine(home, ".config", "@vibeminer", "desktop", DataFolders.ChoiceFile);

    private void Choose(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ChoiceFile)!);
        File.WriteAllText(ChoiceFile, text);
    }

    [Fact]
    public async Task TheFlagThenTheEnvironmentThenTheChoiceThenTheDefault()
    {
        Choose("""{"version":1,"path":"/mnt/big/../big/VibeMiner"}""");
        var env = Linux(("VIBEMINER_DATA_DIR", "/qa/data"));
        Assert.Equal(new DataFolder("/flag", DataFolderSource.Flag), await DataFolders.ResolveAsync(env, "/flag", default));
        Assert.Equal(new DataFolder("/qa/data", DataFolderSource.Override), await DataFolders.ResolveAsync(env, null, default));
        Assert.Equal(new DataFolder("/mnt/big/VibeMiner", DataFolderSource.Chosen), await DataFolders.ResolveAsync(Linux(), null, default));
        File.Delete(ChoiceFile);
        Assert.Equal(new DataFolder(Path.Combine(home, ".local", "share", "VibeMiner"), DataFolderSource.Default), await DataFolders.ResolveAsync(Linux(), null, default));
    }

    [Theory]
    [InlineData("not json", "not valid JSON")]
    [InlineData("[1]", "not an object")]
    [InlineData("""{"version":2,"path":"/x"}""", "newer VibeMiner")]
    [InlineData("""{"version":1,"path":"/x","extra":true}""", "unknown shape")]
    [InlineData("""{"path":"/x"}""", "unknown shape")]
    [InlineData("""{"version":1,"path":"relative/x"}""", "absolute folder")]
    [InlineData("""{"version":1}""", "absolute folder")]
    public async Task AChoiceThatCantBeUsedIsAProblemNeverAFallback(string text, string problem)
    {
        Choose(text);
        var result = Assert.IsType<DataFolderProblem>(await DataFolders.ResolveAsync(Linux(), null, default));
        Assert.Contains(problem, result.Problem);
        Assert.Equal(ChoiceFile, result.File);
    }

    [Fact]
    public async Task AnOversizedChoiceIsRefusedUnread()
    {
        Choose("{\"version\":1,\"path\":\"/" + new string('x', 70_000) + "\"}");
        Assert.Contains("too large", Assert.IsType<DataFolderProblem>(await DataFolders.ResolveAsync(Linux(), null, default)).Problem);
    }

    [Fact]
    public void UserDataIsElectronsFolderForTheDesktopApp()
    {
        Assert.Equal(Path.Combine(home, ".config", "@vibeminer", "desktop"), DataFolders.UserData(Linux()));
        Assert.Equal("/xdg/config/@vibeminer/desktop", DataFolders.UserData(Linux(("XDG_CONFIG_HOME", "/xdg/config"))));
        Assert.Equal(Path.Combine(home, ".config", "@vibeminer", "desktop"), DataFolders.UserData(Linux(("XDG_CONFIG_HOME", "relative"))));
        Assert.Equal("/Users/a/Library/Application Support/@vibeminer/desktop", DataFolders.UserData(new("darwin", _ => null, "/Users/a")));
        Assert.Equal(@"C:\Users\a\AppData\Roaming\@vibeminer\desktop", DataFolders.UserData(new("win32", n => n == "APPDATA" ? @"C:\Users\a\AppData\Roaming" : null, @"C:\Users\a")));
    }

    [Fact]
    public void DefaultsPerOperatingSystem()
    {
        Assert.Equal("/xdg/data/VibeMiner", DataFolders.Default(Linux(("XDG_DATA_HOME", "/xdg/data"))));
        Assert.Equal(Path.Combine(home, ".local", "share", "VibeMiner"), DataFolders.Default(Linux(("XDG_DATA_HOME", "rel"))));
        Assert.Equal("/Users/a/Library/Application Support/VibeMiner", DataFolders.Default(new("darwin", _ => null, "/Users/a")));
        Assert.Equal(@"D:\Local\VibeMiner", DataFolders.Default(new("win32", n => n == "LOCALAPPDATA" ? @"D:\Local" : null, @"C:\Users\a")));
        Assert.Equal(@"C:\Users\a\AppData\Local\VibeMiner", DataFolders.Default(new("win32", _ => null, @"C:\Users\a")));
    }

    [Fact]
    public void WindowsChoicesAreNormalizedWithBackslashes()
    {
        Assert.Equal(@"E:\Data\VibeMiner", DataFolders.Normalize("win32", @"E:/Data\old\..\VibeMiner\"));
        Assert.True(DataFolders.IsAbsolute("win32", @"\\server\share"));
        Assert.False(DataFolders.IsAbsolute("win32", @"Data\VibeMiner"));
    }
}
