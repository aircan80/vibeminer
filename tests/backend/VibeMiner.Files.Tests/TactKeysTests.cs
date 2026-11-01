using Xunit;

namespace VibeMiner.Files.Tests;

public sealed class TactKeysTests
{
    [Fact]
    public void An_unknown_name_finds_no_key()
    {
        Assert.Null(TactKeys.None.Find(1));
        var keys = new TactKeys().Add(0x0123456789ABCDEF, new byte[16]);
        Assert.Null(keys.Find(0x0123456789ABCDEE));
        Assert.Equal(16, keys.Find(0x0123456789ABCDEF)!.Value.Length);
    }

    [Fact]
    public void A_key_is_copied_and_must_be_16_bytes()
    {
        var key = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        var keys = new TactKeys().Add(7, key);
        key[0] = 99;
        Assert.Equal(1, keys.Find(7)!.Value.Span[0]);
        Assert.Throws<ArgumentException>(() => new TactKeys().Add(7, new byte[15]));
    }
}
