using System.Text;
using VibeMiner.Builds.Formats;
using VibeMiner.Cdn;
using Xunit;

namespace VibeMiner.Builds.Tests.Formats;

public sealed class TactConfigTests
{
    private const string A = "0123456789abcdef0123456789abcdef", B = "fedcba9876543210fedcba9876543210";
    private const string C = "00000000000000000000000000000001", D = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void KeepsUnknownFieldsEmptyValuesAndTokenOrder()
    {
        var config = Parse(" # build config\r\n\n encoding = first\t second \r\nunknown-9 =\nopaque = hash # literal\n");
        Assert.Equal(["first", "second"], config.Fields["encoding"]);
        Assert.Empty(config.Fields["unknown-9"]);
        Assert.Equal(["hash", "#", "literal"], config.Fields["opaque"]);
        Assert.Equal(3, config.Fields.Count);
    }

    [Theory]
    [InlineData("a=b\na=c")]
    [InlineData("a")]
    [InlineData("a=b=c")]
    [InlineData("=b")]
    [InlineData("Upper=b")]
    [InlineData("-a=b")]
    [InlineData("a_b=c")]
    [InlineData("a=b\0")]
    [InlineData("# bad\u0001")]
    [InlineData("a=b\rnext=c")]
    [InlineData("a=b\r")]
    [InlineData("\ufeffa=b")]
    public void RejectsMalformedAssignmentsAndControls(string source)
    {
        var error = Assert.Throws<BuildFormatException>(() => Parse(source));
        Assert.Equal((BuildFailure.Corrupt, "CONFIG_INVALID"), (error.Failure, error.Code));
    }

    [Fact]
    public void RejectsInvalidUtf8() =>
        Assert.Equal("CONFIG_INVALID", Assert.Throws<BuildFormatException>(() => TactConfig.Parse([0xff])).Code);

    [Fact]
    public void BoundsBytesLinesFieldsAndValues()
    {
        AssertLimit(() => TactConfig.Parse(new byte[TactConfig.MaxBytes + 1]));
        AssertLimit(() => Parse(new string('\n', TactConfig.MaxLines + 1)));
        AssertLimit(() => Parse("#" + new string('x', TactConfig.MaxLineChars)));
        AssertLimit(() => Parse(string.Concat(Enumerable.Range(0, TactConfig.MaxFields + 1).Select(i => $"k{i}=\n"))));
        AssertLimit(() => Parse("a=" + string.Join(' ', Enumerable.Repeat("x", TactConfig.MaxValuesPerField + 1))));
    }

    [Fact]
    public void EmptyIsValidButCancellationIsObserved()
    {
        Assert.Empty(Parse("").Fields);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => TactConfig.Parse([], canceled.Token));
    }

    [Fact]
    public void ReadsABuildConfig()
    {
        var config = Build($"# Build Configuration\nroot = {A}\ninstall = {B} {C}\ninstall-size = 10 20\ndownload = {C}\n" +
            $"encoding = {A} {B}\nencoding-size = 300 200\nsize = {D} {A}\nsize-size = 5 6\nbuild-name = WOW-1patch1.0_Test\nvfs-root = {A} {B}\n");
        Assert.Equal(CdnKey.Parse(A), config.Root);
        Assert.Equal(new KeyPair(CdnKey.Parse(A), CdnKey.Parse(B)), config.Encoding);
        Assert.Equal(new SizePair(300, 200), config.EncodingSize);
        Assert.Equal(new ConfigKeys(CdnKey.Parse(B), CdnKey.Parse(C)), config.Install);
        Assert.Equal(new SizePair(10, 20), config.InstallSize);
        Assert.Equal(new ConfigKeys(CdnKey.Parse(C), null), config.Download);
        Assert.Null(config.DownloadSize);
        Assert.Equal(new ConfigKeys(CdnKey.Parse(D), CdnKey.Parse(A)), config.Size);
        Assert.Equal(new SizePair(5, 6), config.SizeSize);
        Assert.Equal("WOW-1patch1.0_Test", config.BuildName);
        Assert.Equal([A, B], config.Fields["vfs-root"]);
    }

    [Fact]
    public void ABuildConfigNeedsOnlyRootEncodingAndInstall()
    {
        var config = Build($"root = {A}\nencoding = {A.ToUpperInvariant()} {B}\nencoding-size = 2 1\ninstall = {C}\nbuild-name =\n");
        Assert.Equal(CdnKey.Parse(B), config.Encoding.EKey);
        Assert.Equal(new SizePair(2, 1), config.EncodingSize);
        Assert.Equal(new ConfigKeys(CdnKey.Parse(C), null), config.Install);
        Assert.Null(config.InstallSize);
        Assert.Null(config.Download);
        Assert.Null(config.Size);
        Assert.Null(config.BuildName);
    }

    private const string Root = $"root = {A}\n", Enc = $"encoding = {A} {B}\nencoding-size = 1 2\n", Inst = $"install = {C}\n";
    private const string Valid = Root + Enc + Inst;

    [Theory]
    [InlineData(Enc + Inst)]
    [InlineData(Root + Inst)]
    [InlineData(Root + Enc)]
    [InlineData($"root =\n" + Enc + Inst)]
    [InlineData($"root = {A} {B}\n" + Enc + Inst)]
    [InlineData(Root + $"encoding = {A}\n" + Inst)]
    [InlineData(Root + $"encoding = {A} {B} {C}\nencoding-size = 1 2\n" + Inst)]
    [InlineData(Root + $"encoding = {A} {B}\n" + Inst)]
    [InlineData($"root = 00000000000000000000000000000000\n" + Enc + Inst)]
    [InlineData($"root = {A}x\n" + Enc + Inst)]
    [InlineData(Root + $"encoding = {A} not-a-key\nencoding-size = 1 2\n" + Inst)]
    [InlineData(Root + $"encoding = {A} {B}\nencoding-size = 1\n" + Inst)]
    [InlineData(Root + $"encoding = {A} {B}\nencoding-size = 1 0\n" + Inst)]
    [InlineData(Root + $"encoding = {A} {B}\nencoding-size = 01 2\n" + Inst)]
    [InlineData(Root + $"encoding = {A} {B}\nencoding-size = -1 2\n" + Inst)]
    [InlineData(Root + $"encoding = {A} {B}\nencoding-size = 1 99999999999999999999\n" + Inst)]
    [InlineData(Root + Enc + "install =\n")]
    [InlineData(Root + Enc + $"install = {B} {C}\n")]
    [InlineData(Valid + "download-size = 1 2\n")]
    [InlineData(Valid + $"download = {A} {B} {C}\n")]
    [InlineData(Valid + $"download = {A} {B}\n")]
    [InlineData(Valid + $"size = {A}\nsize-size = 1 2 3\n")]
    public void MalformedBuildConfigsAreCorrupt(string source)
    {
        var error = Assert.Throws<BuildFormatException>(() => Build(source));
        Assert.Equal((BuildFailure.Corrupt, "BUILD_CONFIG_INVALID"), (error.Failure, error.Code));
    }

    [Theory]
    [InlineData(Root + $"encoding = {A} {B}\nencoding-size = 1073741825 2\n" + Inst)]
    [InlineData(Root + $"encoding = {A} {B}\nencoding-size = 1 536870913\n" + Inst)]
    [InlineData(Valid + $"size = {A} {B}\nsize-size = 1073741825 1\n")]
    public void SizesOverTheBlteLimitsAreRefused(string source)
    {
        var error = Assert.Throws<BuildFormatException>(() => Build(source));
        Assert.Equal((BuildFailure.Corrupt, "BUILD_CONFIG_LIMIT"), (error.Failure, error.Code));
        Assert.Equal(1L << 30, Build(Root + $"encoding = {A} {B}\nencoding-size = 1073741824 536870912\n" + Inst).EncodingSize.Decoded);
    }

    [Fact]
    public void AVfsRootSizeIsUnsupported()
    {
        var error = Assert.Throws<BuildFormatException>(() => Build(Valid + "root-size = 1 2\n"));
        Assert.Equal((BuildFailure.Unsupported, "BUILD_CONFIG_UNSUPPORTED"), (error.Failure, error.Code));
    }

    [Fact]
    public void BuildConfigGrammarFailuresKeepTheirCodes() =>
        Assert.Equal("CONFIG_INVALID", Assert.Throws<BuildFormatException>(() => Build($"root = {A}\nroot = {A}\n")).Code);

    [Fact]
    public void ReadsACdnConfigInFileOrder()
    {
        var config = Cdn($"# CDN Configuration\narchives = {B} {A} {C}\narchives-index-size = 30 10 20\narchive-group = {D}\n" +
            $"patch-archives = {A}\nfile-index = {C}\nfile-index-size = 4096\n");
        Assert.Equal([CdnKey.Parse(B), CdnKey.Parse(A), CdnKey.Parse(C)], config.Archives);
        Assert.Equal([30L, 10L, 20L], config.ArchiveIndexSizes!);
        Assert.Equal(CdnKey.Parse(D), config.ArchiveGroup);
        Assert.Equal(CdnKey.Parse(C), config.FileIndex);
        Assert.Equal(4096, config.FileIndexSize);
        Assert.Equal([A], config.Fields["patch-archives"]);
    }

    [Fact]
    public void ACdnConfigMayListNoArchives()
    {
        var config = Cdn("archives =\n");
        Assert.Empty(config.Archives);
        Assert.Null(config.ArchiveIndexSizes);
        Assert.Null(config.FileIndex);
        Assert.Null(config.FileIndexSize);
        Assert.Null(config.ArchiveGroup);
    }

    [Theory]
    [InlineData($"archive-group = {A}\n")]
    [InlineData($"archives = {A} nope\n")]
    [InlineData($"archives = {A} 00000000000000000000000000000000\n")]
    [InlineData($"archives = {A} {B}\narchives-index-size = 10\n")]
    [InlineData($"archives = {A}\narchives-index-size = 0\n")]
    [InlineData($"archives = {A}\nfile-index = {A} {B}\n")]
    [InlineData($"archives = {A}\nfile-index =\n")]
    [InlineData($"archives = {A}\nfile-index-size = 10\n")]
    [InlineData($"archives = {A}\nfile-index = {B}\nfile-index-size = x\n")]
    [InlineData($"archives = {A}\narchive-group = {A} {B}\n")]
    public void MalformedCdnConfigsAreCorrupt(string source)
    {
        var error = Assert.Throws<BuildFormatException>(() => Cdn(source));
        Assert.Equal((BuildFailure.Corrupt, "CDN_CONFIG_INVALID"), (error.Failure, error.Code));
    }

    private static TactConfig Parse(string source) => TactConfig.Parse(Encoding.UTF8.GetBytes(source));
    private static BuildConfigFile Build(string source) => BuildConfigFile.Parse(Encoding.UTF8.GetBytes(source));
    private static CdnConfigFile Cdn(string source) => CdnConfigFile.Parse(Encoding.UTF8.GetBytes(source));
    private static void AssertLimit(Action action) => Assert.Equal("CONFIG_LIMIT", Assert.Throws<BuildFormatException>(action).Code);
}
