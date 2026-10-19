using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Protocols.Tact;

namespace VibeMiner.Infrastructure;

/// <summary>Single-instance bounded icon derivation/read service. No acquisition or research resolution.
/// Successful publication and subsequent catalogue registration remain separate recoverable resources.</summary>
public sealed partial class IconArtifacts(VerifiedObjectStore objects)
{
    public const string Revision = "icon-png-proof-v1";
    public const string PixelPolicy = "rgba8-top-left-straight-no-colorspace-conversion-v1";
    public const string ContentType = "image/png";
    public const long MaxSourceBytes = 8L << 20;
    public const long MaxPngBytes = 5L << 20;
    public const int MaxDimension = 1024, MaxPixels = 1_048_576;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 8 };
    private int building, readers;
    private static DiscoveryException Error(string code, string message) => new("ASSET_" + code, message);

    public static string ComputeKey(ContentReceipt source, int mipLevel = 0) => RequestKey(SourceBinding(source), mipLevel);
    internal static byte[] ProofBytes(IconPngProof proof) => JsonSerializer.SerializeToUtf8Bytes(proof, Json);
    internal static string ProofId(IconPngProof proof) => Convert.ToHexStringLower(SHA256.HashData(ProofBytes(proof)));

    public Task<IconPngArtifact> BuildAsync(ContentReceipt source, int mipLevel = 0, CancellationToken token = default) =>
        BuildCoreAsync(source, mipLevel, null, null, null, token);
    public Task<IconPngArtifact> BuildAsync(ContentReceipt source, int mipLevel, Func<string, Task> progress,
        CancellationToken token = default)
    { ArgumentNullException.ThrowIfNull(progress); return BuildCoreAsync(source, mipLevel, null, null, progress, token); }

    internal Task<IconPngArtifact> BuildAsync(ContentReceipt source, int mipLevel, AcquisitionResources resources,
        IAcquisitionWorkContext? workContext, Func<string, Task>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(resources);
        return BuildCoreAsync(source, mipLevel, resources, workContext, progress, token);
    }

    private async Task<IconPngArtifact> BuildCoreAsync(ContentReceipt source, int mipLevel,
        AcquisitionResources? resources, IAcquisitionWorkContext? workContext, Func<string, Task>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var binding = SourceBinding(source); var key = RequestKey(binding, mipLevel);
        if (Interlocked.CompareExchange(ref building, 1, 0) != 0)
            throw new DiscoveryException("ASSET_BUSY", "Another icon is being derived; retry through the bounded shared demand owner.", true);
        string? decodedPath = null, pngPath = null;
        try
        {
            if (progress is not null) await progress("Verifying icon source"); token.ThrowIfCancellationRequested();
            Blp2Image image;
            await using (var encoded = await OpenObjectAsync(binding.EncodedSha256, binding.EncodedBytes, resources, workContext, token))
            await using (var decoded = await objects.CreateStagingFileAsync(token))
            {
                decodedPath = decoded.Name;
                // A caller-provided full-content flag is insufficient: replay physical SHA, EKey,
                // complete BLTE framing/EOF and CKey against retained encoded bytes before derivation.
                // Finite legacy parser units retain their CPU/I/O scope; CAS and progress stay outside.
                using (resources is null ? null : await resources.EnterCpuAsync(workContext, token))
                    await BlteDecoder.DecodeAsync(encoded, decoded, binding.EncodingKey, binding.ContentKey,
                        binding.EncodedBytes, binding.DecodedBytes, token);
                await decoded.FlushAsync(token); decoded.Position = 0;
                if (progress is not null) await progress("Decoding icon pixels"); token.ThrowIfCancellationRequested();
                using (resources is null ? null : await resources.EnterCpuAsync(workContext, token))
                    image = await Blp2Decoder.DecodeAsync(decoded, mipLevel,
                        new(MaxSourceBytes, MaxDimension, MaxPixels, 4L << 20, 4L << 20), token);
            }
            File.Delete(decodedPath); decodedPath = null;
            IconPngProof proof; byte[] proofBytes; string id;
            using (resources is null ? null : await resources.EnterCpuAsync(workContext, token))
            {
                var rgbaSha = Convert.ToHexStringLower(SHA256.HashData(image.Rgba));
                proof = new IconPngProof(key, binding, Blp2Decoder.Revision, PngRgbaEncoder.Revision, PixelPolicy,
                    mipLevel, image.Width, image.Height, image.BaseWidth, image.BaseHeight, image.Encoding.ToString(),
                    image.AlphaDepth, image.AlphaEncoding, image.MipFlags, rgbaSha);
                ValidateProof(proof); proofBytes = ProofBytes(proof); id = ProofId(proof);
            }
            if (progress is not null) await progress("Encoding verified icon PNG"); token.ThrowIfCancellationRequested();
            long length; string expectedSha;
            await using (var png = await objects.CreateStagingFileAsync(token))
            {
                pngPath = png.Name;
                using (resources is null ? null : await resources.EnterCpuAsync(workContext, token))
                    await PngRgbaEncoder.WriteAsync(png, image.Width, image.Height, image.Rgba, proofBytes, token);
                await png.FlushAsync(token); length = png.Length; png.Position = 0;
                using (resources is null ? null : await resources.EnterCpuAsync(workContext, token))
                {
                    await PngRgbaEncoder.ValidateAsync(png, proof, proofBytes, token); png.Position = 0;
                    expectedSha = Convert.ToHexStringLower(await SHA256.HashDataAsync(png, token));
                }
            }
            if (progress is not null) await progress("Publishing verified icon PNG"); token.ThrowIfCancellationRequested();
            var sha = resources is null ? await objects.PublishFileAsync(pngPath, length, token)
                : await objects.PublishFileAsync(pngPath, length, resources, workContext, token);
            pngPath = null;
            if (sha != expectedSha) throw Error("INTEGRITY", "The staged icon changed after complete PNG verification.");
            // Reopening also catches mutation between publication hashing and its file promotion.
            await using var published = await OpenObjectAsync(sha, length, resources, workContext, token);
            return new(key, id, sha, length, proof);
        }
        finally
        {
            try { if (decodedPath is not null) File.Delete(decodedPath); }
            finally { try { if (pngPath is not null) File.Delete(pngPath); } finally { Volatile.Write(ref building, 0); } }
        }
    }

    public Task<Stream> OpenVerifiedAsync(IconPngArtifact artifact, CancellationToken token = default) =>
        OpenCoreAsync(artifact, null, null, token);

    internal Task<Stream> OpenVerifiedAsync(IconPngArtifact artifact, AcquisitionResources resources,
        IAcquisitionWorkContext? workContext, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(resources);
        return OpenCoreAsync(artifact, resources, workContext, token);
    }

    private Task<FileStream> OpenObjectAsync(string sha, long length, AcquisitionResources? resources,
        IAcquisitionWorkContext? workContext, CancellationToken token) => resources is null
        ? objects.OpenVerifiedAsync(sha, length, token)
        : objects.OpenVerifiedAsync(sha, length, resources, workContext, token);

    private async Task<Stream> OpenCoreAsync(IconPngArtifact artifact, AcquisitionResources? resources,
        IAcquisitionWorkContext? workContext, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(artifact); token.ThrowIfCancellationRequested(); ValidateProof(artifact.Proof);
        if (artifact.RequestKey != artifact.Proof.RequestKey || artifact.ArtifactId != ProofId(artifact.Proof)
            || !Hash(artifact.Sha256, 64) || artifact.ByteLength is < 1 or > MaxPngBytes)
            throw Error("INVALID", "The icon descriptor differs from its immutable proof identity.");
        if (Interlocked.Increment(ref readers) > 4)
        { Interlocked.Decrement(ref readers); throw new DiscoveryException("ASSET_BUSY", "The bounded icon read leases are occupied.", true); }
        FileStream? input = null; var transferred = false;
        try
        {
            input = await OpenObjectAsync(artifact.Sha256, artifact.ByteLength, resources, workContext, token);
            using (resources is null ? null : await resources.EnterCpuAsync(workContext, token))
                await PngRgbaEncoder.ValidateAsync(input, artifact.Proof, ProofBytes(artifact.Proof), token);
            input.Position = 0;
            var lease = new ReadLease(input, () => Interlocked.Decrement(ref readers)); transferred = true; return lease;
        }
        finally { if (!transferred) { try { if (input is not null) await input.DisposeAsync(); } finally { Interlocked.Decrement(ref readers); } } }
    }

    private static IconSourceBinding SourceBinding(ContentReceipt source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Projection is not null || source.VerifierRevision != "content-blte-v1")
            throw Error("SOURCE_UNSUPPORTED", "Icon derivation currently requires complete strict BLTE/CKey source proof.");
        var binding = new IconSourceBinding(source.ContentKey, source.EncodingKey, source.Sha256,
            Number(source.EncodedBytes), Number(source.DecodedBytes), source.VerifierRevision);
        ValidateBinding(binding); return binding;
    }
    private static long Number(string value)
    {
        if (value is null || value.Length is < 1 or > 10 || value[0] == '0' || value.Any(ch => ch is < '0' or > '9')
            || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            throw Error("INVALID", "Icon source lengths must be canonical positive decimal integers.");
        return number;
    }
    private static bool Hash(string? value, int digits) => value is not null && value.Length == digits && value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void ValidateBinding(IconSourceBinding binding)
    {
        if (binding is null || !Hash(binding.ContentKey, 32) || !Hash(binding.EncodingKey, 32) || !Hash(binding.EncodedSha256, 64)
            || binding.VerifierRevision != "content-blte-v1" || binding.EncodedBytes < 9 || binding.DecodedBytes < 148)
            throw Error("INVALID", "The icon source binding is not canonical full-content evidence.");
        if (binding.EncodedBytes > MaxSourceBytes || binding.DecodedBytes > MaxSourceBytes)
            throw Error("LIMIT", "The icon source exceeds its encoded/decoded byte budget.");
    }
    private static string RequestKey(IconSourceBinding binding, int mip)
    {
        ValidateBinding(binding); if (mip is < 0 or > 15) throw Error("INVALID", "Choose an explicitly present supported icon mip.");
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { revision = Revision, source = binding, mip, decoderRevision = Blp2Decoder.Revision, encoderRevision = PngRgbaEncoder.Revision, pixelPolicy = PixelPolicy }, Json)));
    }
    private static void ValidateProof(IconPngProof proof)
    {
        if (proof is null) throw Error("INVALID", "An icon proof is required.");
        ValidateBinding(proof.Source);
        if (proof.DecoderRevision != Blp2Decoder.Revision || proof.EncoderRevision != PngRgbaEncoder.Revision || proof.PixelPolicy != PixelPolicy)
            throw Error("INTERPRETATION_STALE", "The icon uses another pixel interpretation or encoder revision.");
        if (proof.RequestKey != RequestKey(proof.Source, proof.MipLevel) || !Hash(proof.RgbaSha256, 64)
            || proof.Width is < 1 or > MaxDimension || proof.Height is < 1 or > MaxDimension
            || proof.BaseWidth is < 1 or > MaxDimension || proof.BaseHeight is < 1 or > MaxDimension
            || proof.Width * (long)proof.Height > MaxPixels || proof.Width != Math.Max(1, proof.BaseWidth >> proof.MipLevel)
            || proof.Height != Math.Max(1, proof.BaseHeight >> proof.MipLevel)
            || proof.MipLevel > 0 && (Math.Max(proof.BaseWidth, proof.BaseHeight) >> (proof.MipLevel - 1)) <= 1
            || proof.MipFlags is not (0 or 1 or 17) || proof.MipLevel > 0 && proof.MipFlags == 0)
            throw Error("INVALID", "The icon proof has inconsistent key/mip/pixel geometry.");
        var admitted = proof.SourceEncoding switch
        {
            "Paletted" => proof.AlphaDepth is 0 or 1 or 4 or 8 && proof.AlphaEncoding == 0,
            "Bc1" => proof.AlphaDepth is 0 or 1 && proof.AlphaEncoding == 0,
            "Bc2" => proof.AlphaDepth is 4 or 8 && proof.AlphaEncoding == 1,
            "Bc3" => proof.AlphaDepth == 8 && proof.AlphaEncoding == 7,
            "Bgra8" => proof.AlphaDepth == 8 && proof.AlphaEncoding == 0,
            _ => false
        };
        if (!admitted) throw Error("INVALID", "The icon proof names an unsupported source encoding combination.");
    }
}
