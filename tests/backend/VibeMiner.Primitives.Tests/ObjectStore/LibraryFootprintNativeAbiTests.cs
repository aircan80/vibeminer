using System.Runtime.InteropServices;
using VibeMiner.Objects;
using Xunit;

namespace VibeMiner.Primitives.Tests;

public sealed class LibraryFootprintNativeAbiTests
{
    // Independent header vectors for O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC.
    // Linux v6.12 arch/arm64/include/uapi/asm/fcntl.h overrides O_DIRECTORY/O_NOFOLLOW as octal 040000/0100000;
    // include/uapi/asm-generic/fcntl.h supplies the x86-64 defaults 00200000/00400000 and common O_CLOEXEC 02000000.
    // https://github.com/torvalds/linux/blob/v6.12/arch/arm64/include/uapi/asm/fcntl.h
    // https://github.com/torvalds/linux/blob/v6.12/include/uapi/asm-generic/fcntl.h
    // Darwin xnu-11215.1.10 bsd/sys/fcntl.h supplies 0x00100000/0x00000100/0x01000000 on both supported architectures.
    // https://github.com/apple-oss-distributions/xnu/blob/xnu-11215.1.10/bsd/sys/fcntl.h
    [Theory]
    [InlineData("linux", Architecture.X64, 0x000b0000)]
    [InlineData("linux", Architecture.Arm64, 0x0008c000)]
    [InlineData("darwin", Architecture.X64, 0x01100100)]
    [InlineData("darwin", Architecture.Arm64, 0x01100100)]
    public void DirectoryOpenFlagsMatchTheTargetKernelAbiEvenOnAnotherHost(string system, Architecture architecture, int expected)
    {
        var platform = system == "linux" ? OSPlatform.Linux : OSPlatform.OSX;
        Assert.Equal(expected, LibraryFootprintNative.DirectoryOpenFlags(platform, architecture));
    }
}
