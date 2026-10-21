using System.Buffers.Binary;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

internal sealed partial class DownloadLedger
{
    /// <summary>
    /// Publishes a new immutable ledger: every member of a proven manifest ledger plus strictly ordered extra members,
    /// none of them manifest members (D066: the encoded objects of ROOT-listed files the manifest leaves out). One
    /// sequential merge, no sort; the manifest ledger itself is unchanged.
    /// </summary>
    internal async Task<DownloadLedgerArtifact> ExtendAsync(DownloadLedgerArtifact manifest, IReadOnlyList<LedgerValue> extra, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(extra);
        long added = 0;
        for (var i = 0; i < extra.Count; i++)
        {
            if (extra[i].Bytes is < 0 or > 0xffffffffffL || i > 0 && extra[i - 1].CompareKey(extra[i]) >= 0)
                throw Error("DOWNLOAD_LEDGER_INTEGRITY", "Supplementary ledger members are not exact, unique and ordered.");
            added = AddTotal(added, extra[i].Bytes);
        }
        if (extra.Count == 0) return manifest;
        var count = checked(manifest.ObjectCount + extra.Count);
        if (count > limits.MaxEntries) throw Error("DOWNLOAD_LEDGER_LIMIT", "The ledger exceeds its admitted member count.");
        var total = checked(manifest.TotalEncodedBytes + added);
        var length = checked(HeaderBytes + count * RecordBytes);
        var files = new LedgerTemporaryFiles(objects, Math.Min(limits.MaxTemporaryBytes, length), 2);
        try
        {
            string path;
            await using (var cursor = await OpenOrderedAsync(manifest, token, resources, workContext))
            await using (var output = await files.CreateAsync(token))
            {
                path = output.Name; files.Reserve(path, HeaderBytes);
                await output.WriteAsync(Header(count, total), token);
                using var writer = new LedgerRowWriter(output, files);
                var next = 0; var more = await cursor.NextAsync(token); long written = 0;
                while (more || next < extra.Count)
                {
                    token.ThrowIfCancellationRequested();
                    if (more && next < extra.Count && cursor.Current.SameKey(extra[next]))
                        throw Error("DOWNLOAD_LEDGER_INTEGRITY", "A supplementary member is already a manifest member.");
                    if (more && (next == extra.Count || cursor.Current.CompareKey(extra[next]) < 0))
                    { await writer.WriteAsync(cursor.Current, token); more = await cursor.NextAsync(token); }
                    else await writer.WriteAsync(extra[next++], token);
                    written++;
                }
                await writer.FlushAsync(token); await output.FlushAsync(token);
                if (written != count || output.Length != length) throw Error("DOWNLOAD_LEDGER_INTEGRITY", "The extended download ledger changed geometry.");
            }
            var sha = resources is null ? await objects.PublishFileAsync(path, length, token)
                : await objects.PublishFileAsync(path, length, resources, workContext, token);
            files.Forget(path);
            var artifact = new DownloadLedgerArtifact(sha, length, count, total); Remember(artifact); return artifact;
        }
        finally { files.Dispose(); }
    }
}
