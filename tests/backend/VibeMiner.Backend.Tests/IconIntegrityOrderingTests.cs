using System.Buffers.Binary;
using System.Security.Cryptography;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class IconIntegrityOrderingTests
{
    [Theory]
    [InlineData("source")]
    [InlineData("build")]
    [InlineData("publish")]
    [InlineData("open")]
    public async Task Source_and_hidden_artifact_paths_release_cpu_before_actual_colliding_stripe_wait(string path)
    {
        await using var f = new IntegrityOrderingFixture();
        var decoded = Blp();
        byte[] encoded = [.. "BLTE"u8, 0, 0, 0, 0, (byte)'N', .. decoded];
        var sha = await f.Publish(encoded);
        var ekey = Convert.ToHexStringLower(MD5.HashData(encoded));
        var ckey = Convert.ToHexStringLower(MD5.HashData(decoded));
        var physical = new EncodedReceipt(ekey, sha, encoded.Length, "https://us.cdn.blizzard.com/tpr/wow/data/authored",
            IntegrityOrderingFixture.Time, IntegrityOrderingFixture.Time, "blte-encoded-v1", new("loose", "0", encoded.Length.ToString(), null));
        var source = new ContentReceipt(ckey, ekey, sha, encoded.Length.ToString(), decoded.Length.ToString(), physical.SourceUrl,
            physical.RetrievedAt, physical.VerifiedAt, SelectedFileAcquirer.VerifierRevision, physical.Location);
        var artifacts = new IconArtifacts(f.Objects);
        // Compatibility derivation establishes the exact expected bytes independently of scheduling mode.
        var expected = await artifacts.BuildAsync(source);
        await f.WhileCollidingStripeIsHeld(path is "publish" or "open" ? expected.Sha256 : sha, async () =>
        {
            if (path == "source")
            {
                var receipt = await ItemIconSourceVerifier.VerifyAsync(f.Objects, f.Resources, null,
                    new("asset", ckey, ekey, encoded.Length, decoded.Length), physical, default);
                Assert.Equal(source with { VerifiedAt = receipt.VerifiedAt }, receipt);
                return;
            }
            IconPngArtifact artifact = expected;
            if (path != "open")
                artifact = await artifacts.BuildAsync(source, 0, f.Resources, null, async _ =>
                {
                    // Every progress callback is outside both parser and CAS CPU scopes.
                    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    using var cpu = await f.Resources.EnterCpuAsync(null, stop.Token);
                }, default);
            Assert.Equal(expected, artifact);
            await using var lease = await artifacts.OpenVerifiedAsync(artifact, f.Resources, null, default);
            Assert.Equal(0, lease.Position);
            using var bytes = new MemoryStream(); await lease.CopyToAsync(bytes);
            Assert.Equal(artifact.ByteLength, bytes.Length);
            Assert.Equal(artifact.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray())));
        });
        Assert.Empty(f.StagingFiles);
    }

    [Fact]
    public async Task Scheduled_progress_failure_releases_build_and_staging_before_retry()
    {
        await using var f = new IntegrityOrderingFixture();
        var decoded = Blp(); byte[] encoded = [.. "BLTE"u8, 0, 0, 0, 0, (byte)'N', .. decoded];
        var sha = await f.Publish(encoded);
        var source = new ContentReceipt(Convert.ToHexStringLower(MD5.HashData(decoded)), Convert.ToHexStringLower(MD5.HashData(encoded)),
            sha, encoded.Length.ToString(), decoded.Length.ToString(), "https://us.cdn.blizzard.com/tpr/wow/data/authored",
            IntegrityOrderingFixture.Time, IntegrityOrderingFixture.Time, SelectedFileAcquirer.VerifierRevision, new("loose", "0", encoded.Length.ToString(), null));
        var artifacts = new IconArtifacts(f.Objects); var expected = new IOException("authored progress failure");
        Assert.Same(expected, await Assert.ThrowsAsync<IOException>(() => artifacts.BuildAsync(source, 0, f.Resources, null,
            stage => stage == "Publishing verified icon PNG" ? Task.FromException(expected) : Task.CompletedTask, default)));
        Assert.Empty(f.StagingFiles);
        var retry = await artifacts.BuildAsync(source, 0, f.Resources, null, null, default);
        await using var read = await artifacts.OpenVerifiedAsync(retry, f.Resources, null, default);
        Assert.Equal(0, read.Position);
    }

    private static byte[] Blp()
    {
        // Original authored 2x2 paletted BLP2 with explicit straight-alpha values.
        var bytes = new byte[1180]; "BLP2"u8.CopyTo(bytes);
        void Put(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
        Put(4, 1); bytes[8] = 1; bytes[9] = 8; Put(12, 2); Put(16, 2); Put(20, 1172); Put(84, 8);
        for (var i = 0; i < 4; i++)
        {
            bytes[148 + (i + 1) * 4] = (byte)(10 + i);
            bytes[149 + (i + 1) * 4] = (byte)(20 + i);
            bytes[150 + (i + 1) * 4] = (byte)(30 + i);
            bytes[1172 + i] = (byte)(i + 1); bytes[1176 + i] = (byte)(i * 64);
        }
        return bytes;
    }
}
