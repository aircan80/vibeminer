using VibeMiner.Formats;
using Xunit;

namespace VibeMiner.Formats.Tests;

public sealed class Db2IndexSizeTests
{
    // Open charges 64 bytes of index budget per declared copy. That covers the copy entry, its resolution and its index
    // entry only while CopyResolution stays 12 bytes; growing it means raising the per-copy charge.
    [Fact]
    public void Copy_resolution_stays_12_bytes()=>
        Assert.Equal(12,System.Runtime.CompilerServices.Unsafe.SizeOf<Db2Reader.CopyResolution>());
}
