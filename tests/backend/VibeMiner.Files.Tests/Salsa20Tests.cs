using VibeMiner.Files.Formats;
using Xunit;

namespace VibeMiner.Files.Tests;

/// <summary>
/// Pins <see cref="Salsa20"/> to the eSTREAM Salsa20/20 verified vectors, Set 1 vector 0, for 128- and 256-bit keys
/// (IV all zero) at stream offsets 0..63 and 192..255, and Set 6 vector 0 (non-zero IV) at 0..63.
/// </summary>
public sealed class Salsa20Tests
{
    private static readonly byte[] Nonce = new byte[8];

    private static byte[] Key(int length)
    {
        var key = new byte[length];
        key[0] = 0x80;
        return key;
    }

    private static byte[] Keystream(byte[] key, int length)
    {
        var data = new byte[length];
        Salsa20.Transform(key, Nonce, data);
        return data;
    }

    private const string Key128At0 =
        "4DFA5E481DA23EA09A31022050859936DA52FCEE218005164F267CB65F5CFD7F2B4F97E0FF16924A52DF269515110A07F9E460BC65EF95DA58F740B7D1DBB0AA";
    private const string Key128At192 =
        "DA9C1581F429E0A00F7D67E23B730676783B262E8EB43A25F55FB90B3E753AEF8C6713EC66C51881111593CCB3E8CB8F8DE124080501EEEB389C4BCB6977CF95";
    private const string Key256At0 =
        "E3BE8FDD8BECA2E3EA8EF9475B29A6E7003951E1097A5C38D23B7A5FAD9F6844B22C97559E2723C7CBBD3FE4FC8D9A0744652A83E72A9C461876AF4D7EF1A117";
    private const string Key256At192 =
        "57BE81F47B17D9AE7C4FF15429A73E10ACF250ED3A90A93C711308A74C6216A9ED84CD126DA7F28E8ABF8BB63517E1CA98E712F4FB2E1A6AED9FDC73291FAA17";

    [Theory]
    [InlineData(16, Key128At0, Key128At192)]
    [InlineData(32, Key256At0, Key256At192)]
    public void KeystreamMatchesTheEstreamVector(int keyLength, string at0, string at192)
    {
        var stream = Keystream(Key(keyLength), 256);
        Assert.Equal(at0, Convert.ToHexString(stream, 0, 64));
        Assert.Equal(at192, Convert.ToHexString(stream, 192, 64));
    }

    [Theory]
    [InlineData("0053A6F94C9FF24598EB3E91E4378ADD3083D6297CCF2275C81B6EC11467BA0D",
        "F5FAD53F79F9DF58C4AEA0D0ED9A9601F278112CA7180D565B420A48019670EAF24CE493A86263F677B46ACE1924773D2BB25571E1AA8593758FC382B1280B71")]
    [InlineData("0053A6F94C9FF24598EB3E91E4378ADD",
        "05E1E7BEB697D999656BF37C1B978806735D0B903A6007BD329927EFBE1B0E2A8137C1AE291493AA83A821755BEE0B06CD14855A67E46703EBF8F3114B584CBA")]
    public void ANonZeroNonceMatchesEstreamSet6(string key, string at0)
    {
        // Set 6 vector 0: every key and nonce byte carries weight.
        var data = new byte[64];
        Salsa20.Transform(Convert.FromHexString(key), Convert.FromHexString("0D74DB42A91077DE"), data);
        Assert.Equal(at0, Convert.ToHexString(data));
    }

    [Theory]
    [InlineData(16, Key128At192)]
    [InlineData(32, Key256At192)]
    public void APartialLastBlockCarriesTheCounterAcrossBlocks(int keyLength, string at192)
    {
        // 200 bytes: three full blocks and 8 bytes of the fourth.
        var stream = Keystream(Key(keyLength), 200);
        Assert.Equal(at192[..16], Convert.ToHexString(stream, 192, 8));
        Assert.Equal(Keystream(Key(keyLength), 256).AsSpan(0, 200).ToArray(), stream);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    public void TransformingTwiceRestoresThePlaintext(int keyLength)
    {
        var plain = new byte[333];
        for (var i = 0; i < plain.Length; i++) plain[i] = (byte)(i * 7 + 3);
        var data = (byte[])plain.Clone();
        Salsa20.Transform(Key(keyLength), Nonce, data);
        Assert.NotEqual(plain, data);
        Salsa20.Transform(Key(keyLength), Nonce, data);
        Assert.Equal(plain, data);
    }

    [Fact]
    public void EmptyDataIsANoOp() => Salsa20.Transform(Key(16), Nonce, Span<byte>.Empty);

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(24)]
    [InlineData(33)]
    public void BadKeyLengthsThrow(int keyLength) =>
        Assert.Throws<ArgumentException>("key", () => Salsa20.Transform(new byte[keyLength], Nonce, new byte[4]));

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(12)]
    public void BadNonceLengthsThrow(int nonceLength) =>
        Assert.Throws<ArgumentException>("nonce", () => Salsa20.Transform(Key(16), new byte[nonceLength], new byte[4]));
}
