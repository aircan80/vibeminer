using System.Security.Cryptography;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Protocols.Tact;

namespace VibeMiner.Infrastructure;

internal enum ObjectPresenceKind { Present, Absent, WrongLength, Unknown }
internal sealed record ObjectPresenceObservation(ObjectPresenceKind Kind, long? ObservedBytes);
internal sealed record ObjectFileIdentity(long Length, DateTime CreatedUtc, DateTime WrittenUtc);

/// <summary>One lazy app-owned immutable CAS. No decoded or per-build copies.</summary>
public sealed class VerifiedObjectStore(string dataDirectory,
    Func<string, long, string, CancellationToken, Task>? recordInvalidity = null) : IAsyncDisposable
{
    public const long MaxObjectBytes = 512L * 1024 * 1024;
    private readonly SemaphoreSlim initialize = new(1, 1);
    private readonly SemaphoreSlim integrityAdmissions = new(128, 128);
    private readonly SemaphoreSlim[] integrityStripes = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly Action<string>? beforeIntegrityStripeWait;
    private FileStream? owner;
    // Test-only arrival signal. The observer must be nonblocking, nonreentrant and nonthrowing;
    // it receives only the SHA identity and must not acquire resources or call back into the store.
    internal VerifiedObjectStore(string dataDirectory,
        Func<string, long, string, CancellationToken, Task>? recordInvalidity, Action<string> observer)
        : this(dataDirectory, recordInvalidity)
    {
        ArgumentNullException.ThrowIfNull(observer);
        beforeIntegrityStripeWait = observer;
    }
    private string Root => Path.Combine(dataDirectory, "objects-v1");
    private string Staging => Path.Combine(dataDirectory, "staging-v1");
    private async Task EnsureInitializedAsync(CancellationToken token)
    {
        await initialize.WaitAsync(token);
        try
        {
            if (owner is not null) return;
            Directory.CreateDirectory(Root); Directory.CreateDirectory(Staging);
            try { owner = new FileStream(Path.Combine(Root, ".owner"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous); }
            catch (IOException) { throw new DiscoveryException("CONTENT_STORE_BUSY", "Another engine owns the content store.", true); }
            // Recovery occurs only while the process owner is held and before this runtime starts publication.
            foreach (var abandoned in Directory.EnumerateFiles(Staging, "configuration-*.part")
                .Concat(Directory.EnumerateFiles(Staging, "metadata-*.part")).Take(64))
                File.Delete(abandoned);
        }
        finally { initialize.Release(); }
    }
    private string ObjectPath(string sha256)
    {
        if (sha256.Length != 64 || sha256.Any(ch => ch is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new DiscoveryException("CATALOG_INTEGRITY", "An object identity is invalid.");
        return Path.Combine(Root, sha256[..2], sha256[2..]);
    }
    /// <summary>Existence/length observation only; never hashes, reads payloads or establishes integrity.</summary>
    internal async Task<ObjectPresenceObservation> ObservePresenceAsync(string sha256, long exactLength, CancellationToken token)
    {
        if (sha256 is null || exactLength is < 1 or > MaxObjectBytes)
            throw new DiscoveryException("CATALOG_INTEGRITY", "A presence observation needs a bounded exact object identity and length.");
        var path = ObjectPath(sha256);
        token.ThrowIfCancellationRequested();
        await EnsureInitializedAsync(token);
        try
        {
            // Refuse links/directories instead of observing bytes outside the app-owned CAS.
            var shard = Path.GetDirectoryName(path)!;
            var shardAttributes = File.GetAttributes(shard);
            if ((shardAttributes & FileAttributes.ReparsePoint) != 0)
                return new(ObjectPresenceKind.Unknown, null);
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                return new(ObjectPresenceKind.Unknown, null);
            var length = new FileInfo(path).Length;
            token.ThrowIfCancellationRequested();
            return new(length == exactLength ? ObjectPresenceKind.Present : ObjectPresenceKind.WrongLength, length);
        }
        catch (FileNotFoundException) { token.ThrowIfCancellationRequested(); return new(ObjectPresenceKind.Absent, null); }
        catch (DirectoryNotFoundException) { token.ThrowIfCancellationRequested(); return new(ObjectPresenceKind.Absent, null); }
        catch (IOException) { token.ThrowIfCancellationRequested(); return new(ObjectPresenceKind.Unknown, null); }
        catch (UnauthorizedAccessException) { token.ThrowIfCancellationRequested(); return new(ObjectPresenceKind.Unknown, null); }
    }
    public async Task<byte[]> ReadAsync(string sha256, int length, CancellationToken token)
    {
        if (length is < 1 or > 1024 * 1024) throw new DiscoveryException("CATALOG_INTEGRITY", "A configuration length is invalid.");
        await EnsureInitializedAsync(token);
        var path = ObjectPath(sha256);
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (input.Length != length) throw new DiscoveryException("LOCAL_CONFIGURATION_CORRUPT", "A saved configuration has changed length.");
            var bytes = new byte[length];
            await input.ReadExactlyAsync(bytes, token);
            if (await input.ReadAsync(new byte[1], token) != 0 || Convert.ToHexStringLower(SHA256.HashData(bytes)) != sha256)
                throw new DiscoveryException("LOCAL_CONFIGURATION_CORRUPT", "A saved configuration failed integrity verification.");
            return bytes;
        }
        catch (FileNotFoundException) { throw new DiscoveryException("LOCAL_CONFIGURATION_MISSING", "A previously saved configuration is missing. No automatic repair was attempted."); }
        catch (DirectoryNotFoundException) { throw new DiscoveryException("LOCAL_CONFIGURATION_MISSING", "A previously saved configuration is missing. No automatic repair was attempted."); }
        catch (EndOfStreamException) { throw new DiscoveryException("LOCAL_CONFIGURATION_CORRUPT", "A saved configuration ended unexpectedly."); }
    }
    public async Task PublishAsync(byte[] verifiedBytes, string sha256, CancellationToken token)
    {
        if (verifiedBytes.Length is < 1 or > 1024 * 1024 || Convert.ToHexStringLower(SHA256.HashData(verifiedBytes)) != sha256)
            throw new DiscoveryException("CONFIG_INTEGRITY", "Configuration bytes changed before publication.");
        await EnsureInitializedAsync(token);
        var destination = ObjectPath(sha256);
        if (File.Exists(destination)) { await ReadAsync(sha256, verifiedBytes.Length, token); return; }
        var staged = Path.Combine(Staging, "configuration-" + Guid.NewGuid().ToString("N") + ".part");
        try
        {
            await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            { await output.WriteAsync(verifiedBytes, token); await output.FlushAsync(token); }
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            try { File.Move(staged, destination); }
            catch (IOException) when (File.Exists(destination)) { await ReadAsync(sha256, verifiedBytes.Length, token); }
            // File publication and catalog association are separately recoverable operations.
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }
    /// <summary>
    /// Creates a unique app-owned metadata staging file. The caller owns the stream and must close
    /// it before publication, or close and delete its Name when abandoning work before publication.
    /// </summary>
    public async Task<FileStream> CreateStagingFileAsync(CancellationToken token)
    {
        await EnsureInitializedAsync(token);
        token.ThrowIfCancellationRequested();
        var path = Path.Combine(Staging, "metadata-" + Guid.NewGuid().ToString("N") + ".part");
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try { token.ThrowIfCancellationRequested(); return stream; }
        catch
        {
            await stream.DisposeAsync();
            File.Delete(path);
            throw;
        }
    }

    /// <summary>The identity of the file behind a verified read lease, so a later open can tell it is the same file.</summary>
    internal static ObjectFileIdentity IdentityOf(FileStream verified) => new(verified.Length,
        File.GetCreationTimeUtc(verified.SafeFileHandle), File.GetLastWriteTimeUtc(verified.SafeFileHandle));

    /// <summary>
    /// Reopens an object whose complete SHA-256 this process already proved through <see cref="OpenVerifiedAsync(string, long, CancellationToken)"/>,
    /// without hashing it again, only while the file at its CAS path keeps the proven length, creation and write times.
    /// Returns null when the file changed, so the caller proves it again. Only the stored-coverage worker uses this, to
    /// read immutable ledgers it already proved (D065); every reader that serves bytes still verifies them.
    /// </summary>
    internal async Task<FileStream?> OpenProvenAsync(string sha256, long length, ObjectFileIdentity proven, CancellationToken token)
    {
        if (length is < 1 or > MaxObjectBytes || proven.Length != length)
            throw new DiscoveryException("CATALOG_INTEGRITY", "An object length is invalid.");
        var path = ObjectPath(sha256);
        await EnsureInitializedAsync(token);
        FileStream? input = null;
        try
        {
            // Refuse links and directories, as presence observation does.
            if ((File.GetAttributes(Path.GetDirectoryName(path)!) & FileAttributes.ReparsePoint) != 0) return null;
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return null;
            input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (IdentityOf(input) == proven) return input;
            await input.DisposeAsync(); return null;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            if (input is not null) await input.DisposeAsync();
            throw new DiscoveryException("LOCAL_OBJECT_MISSING", "A previously saved object is missing. No automatic repair was attempted.");
        }
        catch { if (input is not null) await input.DisposeAsync(); throw; }
    }

    /// <summary>
    /// Verifies exact length and complete local SHA-256 in bounded asynchronous reads, then returns
    /// the caller-owned read lease at offset zero. This is local CAS proof, not a BLTE/CKey proof.
    /// </summary>
    public Task<FileStream> OpenVerifiedAsync(string sha256, long length, CancellationToken token) =>
        OpenVerifiedCoreAsync(sha256, length, null, null, token);

    // Dormant until all production CPU -> CAS/reader edges have been migrated together.
    internal Task<FileStream> OpenVerifiedAsync(string sha256, long length, AcquisitionResources resources,
        IAcquisitionWorkContext? workContext, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(resources);
        return OpenVerifiedCoreAsync(sha256, length, resources, workContext, token);
    }

    private async Task<FileStream> OpenVerifiedCoreAsync(string sha256, long length, AcquisitionResources? resources,
        IAcquisitionWorkContext? workContext, CancellationToken token)
    {
        if (length is < 1 or > MaxObjectBytes)
            throw new DiscoveryException("CATALOG_INTEGRITY", "An object length is invalid.");
        _ = ObjectPath(sha256);
        using var admission = EnterIntegrity(token);
        await EnsureInitializedAsync(token);
        var stripe = IntegrityStripe(sha256);
        beforeIntegrityStripeWait?.Invoke(sha256);
        await stripe.WaitAsync(token);
        try { return await OpenVerifiedUnderLockAsync(sha256, length, resources, workContext, token); }
        finally { stripe.Release(); }
    }

    private async Task<FileStream> OpenVerifiedUnderLockAsync(string sha256, long length, AcquisitionResources? resources,
        IAcquisitionWorkContext? workContext, CancellationToken token)
    {
        var path = ObjectPath(sha256);
        FileStream? input = null;
        try
        {
            input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            try
            {
                var actual = await HashFileAsync(input, length, "LOCAL_OBJECT_CORRUPT", resources, workContext, token);
                if (actual != sha256)
                    throw new DiscoveryException("LOCAL_OBJECT_CORRUPT", "A saved object failed integrity verification.");
            }
            catch (DiscoveryException failure) when (failure.Code == "LOCAL_OBJECT_CORRUPT")
            {
                if (recordInvalidity is not null)
                {
                    // A detected fact belongs to the engine, not to the canceled requesting view.
                    // The injected adapter must perform a bounded durable write, never recurse into
                    // this store. Holding the stripe prevents delayed old-file journal writes after repair.
                    try { await recordInvalidity(sha256, length, "LOCAL_OBJECT_CORRUPT", CancellationToken.None); }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    { throw new DiscoveryException("CONTENT_INTEGRITY_JOURNAL_UNAVAILABLE", "Detected object corruption could not be recorded durably.", true); }
                }
                throw;
            }
            token.ThrowIfCancellationRequested();
            input.Position = 0;
            return input;
        }
        catch (FileNotFoundException)
        {
            if (input is not null) await input.DisposeAsync();
            throw new DiscoveryException("LOCAL_OBJECT_MISSING", "A previously saved object is missing. No automatic repair was attempted.");
        }
        catch (DirectoryNotFoundException)
        {
            if (input is not null) await input.DisposeAsync();
            throw new DiscoveryException("LOCAL_OBJECT_MISSING", "A previously saved object is missing. No automatic repair was attempted.");
        }
        catch
        {
            if (input is not null) await input.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Engine-only encoded publication/repair. Caller supplies shared CPU/staging admission. This
    /// method independently checks the complete BLTE encoded proof and SHA before any replacement;
    /// it does not establish CKey/readability or clear a durable corruption observation.
    /// </summary>
    internal Task<string> PublishEncodedFileAsync(string appOwnedStagedPath, long encodedBytes,
        string encodingKey, CancellationToken token) =>
        PublishEncodedFileCoreAsync(appOwnedStagedPath, encodedBytes, encodingKey, null, null, token);

    internal Task<string> PublishEncodedFileAsync(string appOwnedStagedPath, long encodedBytes,
        string encodingKey, AcquisitionResources resources, IAcquisitionWorkContext? workContext, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(resources);
        return PublishEncodedFileCoreAsync(appOwnedStagedPath, encodedBytes, encodingKey, resources, workContext, token);
    }

    private async Task<string> PublishEncodedFileCoreAsync(string appOwnedStagedPath, long encodedBytes,
        string encodingKey, AcquisitionResources? resources, IAcquisitionWorkContext? workContext, CancellationToken token)
    {
        var staged = ValidateMetadataStagingPath(appOwnedStagedPath);
        var cleanup = owner is not null;
        IDisposable? admission = null;
        try
        {
            if (encodedBytes is < 9 or > MaxObjectBytes || encodingKey is not { Length: 32 } ||
                encodingKey.Any(ch => ch is not (>= '0' and <= '9' or >= 'a' and <= 'f')) || encodingKey.All(ch => ch == '0'))
                throw new DiscoveryException("CONTENT_REPAIR_INVALID", "Encoded publication requires a full exact supported identity and length.");
            admission = EnterIntegrity(token);
            await EnsureInitializedAsync(token); cleanup = true;
            await using var incoming = new FileStream(staged, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            // Explicit legacy parser/I/O exception: this existing finite BLTE/MD5 proof has no
            // resource/CAS/row callbacks. It is CPU-admitted, not a <=1MiB cooperative unit. Release
            // before the SHA quanta and stripe below; the resource-aware route forbids outer CPU ownership.
            using (resources is null ? null : await resources.EnterCpuAsync(workContext, token))
            {
                token.ThrowIfCancellationRequested();
                await BlteEncodedVerifier.VerifyAsync(incoming, encodingKey, encodedBytes, token);
            }
            incoming.Position = 0;
            var sha256 = await HashFileAsync(incoming, encodedBytes, "CONTENT_INTEGRITY", resources, workContext, token);
            var destination = ObjectPath(sha256);
            var stripe = IntegrityStripe(sha256);
            beforeIntegrityStripeWait?.Invoke(sha256);
            await stripe.WaitAsync(token);
            try
            {
                // Fixed stripes bound key-lock storage; admission bounds waiters. Verification and
                // invalidity journaling use the same stripe, so a pre-repair reader cannot journal late.
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    if (CanonicalDestinationExists(destination))
                    {
                        try
                        {
                            await using var valid = await OpenVerifiedUnderLockAsync(sha256, encodedBytes, resources, workContext, token);
                            return sha256;
                        }
                        catch (DiscoveryException failure) when (failure.Code == "LOCAL_OBJECT_CORRUPT")
                        {
                            token.ThrowIfCancellationRequested();
                            _ = CanonicalDestinationExists(destination);
                            try
                            {
                                // File.Replace requires an existing destination and same-volume atomic
                                // replacement. No delete-first gap, cross-volume copy fallback or backup.
                                File.Replace(staged, destination, null, ignoreMetadataErrors: false);
                                return sha256; // Atomic publication may win a cancellation race.
                            }
                            catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                            { throw new DiscoveryException("CONTENT_REPAIR_UNAVAILABLE", "Verified object replacement is unavailable; existing bytes were preserved.", true); }
                        }
                        catch (DiscoveryException failure) when (failure.Code == "LOCAL_OBJECT_MISSING")
                        { /* A concurrent path removal may still permit ordinary new publication. */ }
                    }
                    token.ThrowIfCancellationRequested();
                    try { File.Move(staged, destination); return sha256; }
                    catch (IOException) when (attempt == 0 && File.Exists(destination))
                    { /* An ordinary publisher raced us; recheck its exact bytes under our stripe. */ }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { throw new DiscoveryException("CONTENT_REPAIR_UNAVAILABLE", "Verified object publication is unavailable.", true); }
                }
                throw new DiscoveryException("CONTENT_REPAIR_UNAVAILABLE", "The canonical object changed during bounded publication.", true);
            }
            finally { stripe.Release(); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        { throw new DiscoveryException("CONTENT_REPAIR_UNAVAILABLE", "Verified encoded publication or replacement could not access its owned files.", true); }
        finally
        {
            try { if (cleanup && File.Exists(staged)) File.Delete(staged); }
            finally { admission?.Dispose(); }
        }
    }

    private bool CanonicalDestinationExists(string destination)
    {
        var rootAttributes = File.GetAttributes(Root);
        if ((rootAttributes & FileAttributes.ReparsePoint) != 0 || (rootAttributes & FileAttributes.Directory) == 0)
            throw new DiscoveryException("CONTENT_REPAIR_UNSAFE", "The canonical object store is not an app-owned regular directory.");
        var shard = Path.GetDirectoryName(destination)!; Directory.CreateDirectory(shard);
        var shardAttributes = File.GetAttributes(shard);
        if ((shardAttributes & FileAttributes.ReparsePoint) != 0 || (shardAttributes & FileAttributes.Directory) == 0)
            throw new DiscoveryException("CONTENT_REPAIR_UNSAFE", "The canonical object shard is not an app-owned regular directory.");
        try
        {
            var attributes = File.GetAttributes(destination);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new DiscoveryException("CONTENT_REPAIR_UNSAFE", "Encoded repair cannot replace links or directories.");
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private SemaphoreSlim IntegrityStripe(string sha256) => integrityStripes[Convert.ToByte(sha256[..2], 16) % integrityStripes.Length];
    private IDisposable EnterIntegrity(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!integrityAdmissions.Wait(0))
            throw new DiscoveryException("CONTENT_STORE_CAPACITY", "The bounded object integrity queue is full.", true);
        return new IntegrityAdmission(integrityAdmissions);
    }
    private sealed class IntegrityAdmission(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? held = semaphore;
        public void Dispose() => Interlocked.Exchange(ref held, null)?.Release();
    }

    /// <summary>
    /// Consumes a closed app-owned metadata staging file, computes its complete local SHA-256 and
    /// publishes/reuses immutable bytes in the one shared CAS. The caller verifies BLTE/source
    /// semantics first. Once this store owns cleanup, success/failure/cancellation removes staging.
    /// File publication and the caller's catalog association remain separate recoverable steps.
    /// </summary>
    public Task<string> PublishFileAsync(string appOwnedStagedPath, long length, CancellationToken token) =>
        PublishFileCoreAsync(appOwnedStagedPath, length, null, null, token);

    internal Task<string> PublishFileAsync(string appOwnedStagedPath, long length, AcquisitionResources resources,
        IAcquisitionWorkContext? workContext, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(resources);
        return PublishFileCoreAsync(appOwnedStagedPath, length, resources, workContext, token);
    }

    private async Task<string> PublishFileCoreAsync(string appOwnedStagedPath, long length, AcquisitionResources? resources,
        IAcquisitionWorkContext? workContext, CancellationToken token)
    {
        var staged = ValidateMetadataStagingPath(appOwnedStagedPath);
        var cleanup = owner is not null;
        IDisposable? admission = null;
        try
        {
            admission = EnterIntegrity(token);
            await EnsureInitializedAsync(token);
            cleanup = true;
            if (length is < 1 or > MaxObjectBytes)
                throw new DiscoveryException("CONTENT_LIMIT", "A staged object length exceeds the admitted limits.");
            // A read handle denies writers while hashing and permits promotion on Windows. The
            // staging path is private to its application operation until this call consumes it.
            await using var input = new FileStream(staged, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var sha256 = await HashFileAsync(input, length, "CONTENT_INTEGRITY", resources, workContext, token);
            var destination = ObjectPath(sha256);
            var stripe = IntegrityStripe(sha256);
            beforeIntegrityStripeWait?.Invoke(sha256);
            await stripe.WaitAsync(token);
            try
            {
                if (File.Exists(destination))
                {
                    // Reuse the operation's admission and scheduling mode, not a second public open.
                    await using var existing = await OpenVerifiedUnderLockAsync(sha256, length, resources, workContext, token);
                    return sha256;
                }
                token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                try { File.Move(staged, destination); }
                catch (IOException) when (File.Exists(destination))
                {
                    await using var existing = await OpenVerifiedUnderLockAsync(sha256, length, resources, workContext, token);
                }
                return sha256;
            }
            finally { stripe.Release(); }
        }
        finally
        {
            // A second store refused by the process owner cannot delete the active owner's work.
            try { if (cleanup && File.Exists(staged)) File.Delete(staged); }
            finally { admission?.Dispose(); }
        }
    }

    private string ValidateMetadataStagingPath(string path)
    {
        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new DiscoveryException("CONTENT_STAGING_INVALID", "A metadata staging path is invalid."); }
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var name = Path.GetFileName(fullPath);
        var identifier = name.StartsWith("metadata-", StringComparison.Ordinal) && name.EndsWith(".part", StringComparison.Ordinal)
            ? name[9..^5] : "";
        if (!string.Equals(Path.GetDirectoryName(fullPath), Path.GetFullPath(Staging), comparison) ||
            identifier.Length != 32 || identifier.Any(ch => !char.IsAsciiHexDigit(ch)) ||
            new FileInfo(fullPath).LinkTarget is not null || new DirectoryInfo(Staging).LinkTarget is not null)
            throw new DiscoveryException("CONTENT_STAGING_INVALID", "Only app-owned metadata staging files can be published.");
        return fullPath;
    }

    internal static async Task<string> HashFileAsync(Stream input, long length, string errorCode,
        AcquisitionResources? resources, IAcquisitionWorkContext? workContext, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (length is < 1 or > MaxObjectBytes || !input.CanRead || !input.CanSeek || input.Position != 0 || input.Length != length)
            throw new DiscoveryException(errorCode, "An object has changed length or is not an exact readable source.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long observed = 0; var bytesSinceYield = 0;
        while (observed < length)
        {
            token.ThrowIfCancellationRequested();
            // Short reads may leave only one byte in the scheduler quantum. Cap the next request
            // by that remainder too, so synchronous cached reads never run past the 1MiB turn.
            var requested = (int)Math.Min(Math.Min(buffer.Length, length - observed), 1048576 - bytesSinceYield);
            var count = await input.ReadAsync(buffer.AsMemory(0, requested), token);
            token.ThrowIfCancellationRequested();
            if (count == 0) throw new DiscoveryException(errorCode, "An object ended before its declared length.");
            using (resources is null ? null : await resources.EnterCpuAsync(workContext, token))
            {
                token.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, count);
            }
            observed += count;
            bytesSinceYield += count;
            if (bytesSinceYield == 1048576)
            {
                await Task.Yield();
                bytesSinceYield = 0;
                token.ThrowIfCancellationRequested();
            }
        }
        token.ThrowIfCancellationRequested();
        var trailing = await input.ReadAsync(buffer.AsMemory(0, 1), token);
        token.ThrowIfCancellationRequested();
        if (trailing != 0)
            throw new DiscoveryException(errorCode, "An object exceeds its declared length.");
        string digest;
        using (resources is null ? null : await resources.EnterCpuAsync(workContext, token))
        {
            token.ThrowIfCancellationRequested();
            digest = Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        token.ThrowIfCancellationRequested();
        return digest;
    }

    public async ValueTask DisposeAsync()
    {
        if (owner is not null) await owner.DisposeAsync();
        initialize.Dispose();
        integrityAdmissions.Dispose();
        foreach (var stripe in integrityStripes) stripe.Dispose();
    }
}
