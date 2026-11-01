using VibeMiner.Files;

namespace VibeMiner.Formats.Images;

/// <summary>
/// How a <see cref="BlpImages"/> call ended. Every state is its own; none stands in for another (D390).
/// </summary>
public enum BlpImageState
{
    /// <summary>Decode: the image is decoded. Inspect: the header was read and its descriptors pass the decoder's checks.</summary>
    Ready,
    /// <summary>BLP_UNSUPPORTED: a well-formed file this decoder doesn't read (BLP1, JPEG, an unseen encoding profile, a compact level).</summary>
    Unsupported,
    /// <summary>BLP_INVALID: the bytes contradict the format, including a file shorter than its 148-byte header.</summary>
    Invalid,
    /// <summary>BLP_MIP_UNAVAILABLE: the level isn't declared, or no declared level fits the side asked for.</summary>
    MipUnavailable,
    /// <summary>BLP_LIMIT: over the decoder limits.</summary>
    LimitExceeded,
    /// <summary>Bytes the call needs lie in holes (TACT keys not known). <c>HoleKeys</c> names them. Nothing is zero-filled.</summary>
    Protected,
    /// <summary>Layer 4 didn't give the bytes: <c>FileState</c> says why (UnknownFile, Missing, NeedsNetwork, Corrupt, TooLarge...).</summary>
    FileFailed,
}

/// <summary>What the generated store did for a decode.</summary>
public enum BlpImageCache
{
    /// <summary>Nothing to cache: the call didn't decode an image.</summary>
    None,
    /// <summary>Read from the generated store; no file bytes were read.</summary>
    Hit,
    /// <summary>Decoded and stored.</summary>
    Stored,
    /// <summary>Decoded but over <see cref="VibeMiner.Generated.GeneratedLimits.MaxObjectBytes"/> with its header, so not stored.</summary>
    TooLarge,
    /// <summary>Decoded, but the store refused the write (a full disk, a closed store). The image is still returned.</summary>
    StoreFailed,
}

/// <summary>
/// Raw header bytes, set whenever the 148-byte header was read and starts <c>BLP2</c> with type 1, even when the decoder refuses
/// it, so a detail view can name an unsupported profile. <see cref="DeclaredMips"/> counts the leading descriptor pairs with a
/// nonzero offset and length, never the mip byte.
/// </summary>
public sealed record BlpHeader(byte ColorEncoding, byte AlphaDepth, byte AlphaEncoding, byte MipFlags, uint Width, uint Height, int DeclaredMips);

/// <summary>
/// The outcome of <see cref="BlpImages.InspectAsync"/>. <see cref="Encoding"/> and <see cref="Mips"/> are set only for Ready.
/// <see cref="Code"/> and <see cref="Message"/> are the decoder's for a refusal and layer 4's for FileFailed.
/// </summary>
public sealed record BlpInspection(BlpImageState State, BlpHeader? Header, Blp2Encoding? Encoding, IReadOnlyList<Blp2Mip>? Mips,
    IReadOnlyList<string> HoleKeys, FileReadState? FileState, string? Code, string? Message, DateTimeOffset? ObservedAt);

/// <summary>
/// The outcome of a decode. <see cref="Image"/> is set only for Ready. <see cref="HoleKeys"/> is set only for Protected and
/// <see cref="FileState"/> only for FileFailed.
/// </summary>
public sealed record BlpImageResult(BlpImageState State, Blp2Image? Image, BlpImageCache Cache, IReadOnlyList<string> HoleKeys,
    FileReadState? FileState, string? Code, string? Message, DateTimeOffset? ObservedAt)
{
    internal static BlpImageResult Decoded(Blp2Image image, BlpImageCache cache) => new(BlpImageState.Ready, image, cache, [], null, null, null, null);

    internal static BlpImageResult From(BlpInspection failed) =>
        new(failed.State, null, BlpImageCache.None, failed.HoleKeys, failed.FileState, failed.Code, failed.Message, failed.ObservedAt);
}
