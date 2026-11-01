using System.Net;

namespace VibeMiner.Cdn;

/// <summary>Why a CDN or version-service fetch failed. Each is a different state; none of them means the object doesn't exist.</summary>
public enum CdnFailure
{
    /// <summary>No connection: name resolution or connect failed on every host.</summary>
    Offline,
    /// <summary>HTTP 404 (or level3's 403 for a missing key). A dated, retryable observation about that host, never absence.</summary>
    NotFound,
    /// <summary>Another HTTP status (5xx, 403, 416 ...). Retryable.</summary>
    HttpStatus,
    /// <summary>No headers within the header timeout, or no bytes within the idle timeout. Retryable.</summary>
    Timeout,
    /// <summary>The connection broke mid-body, or the body ended before the length its headers declared. Retryable.</summary>
    Network,
    /// <summary>A 200 to a range request; aborted as soon as its headers arrived, the body is never read. Retryable on another host.</summary>
    RangeIgnored,
    /// <summary>A 206 whose Content-Range isn't exactly the requested span, or a 206 to a whole request.</summary>
    RangeMismatch,
    /// <summary>Content-Length, Content-Range total or the received length differs from the expected length (a body cut short of its declared headers is Network).</summary>
    SizeConflict,
    /// <summary>The bytes don't pass their key type's check. The code is the verifier's refusal code.</summary>
    VerificationFailed,
    /// <summary>A redirect, a Content-Encoding or a media type the CDN doesn't serve for these objects.</summary>
    Protocol,
    /// <summary>The reply is larger than the client accepts in memory.</summary>
    TooLarge,
}

/// <summary>One host's answer to one request, kept so a caller can see what every host said.</summary>
public sealed record CdnAttempt(string Host, Uri Url, CdnFailure Failure, HttpStatusCode? Status, DateTimeOffset ObservedAt, string Message, string? Code = null);

/// <summary>Every host of the route failed; <see cref="Failure"/> is the last host's failure, <see cref="Attempts"/> has them all.</summary>
public sealed class CdnException : Exception
{
    public CdnException(IReadOnlyList<CdnAttempt> attempts)
        : base(attempts.Count == 0 ? "No CDN host was tried." : attempts[^1].Message)
    {
        if (attempts.Count == 0) throw new ArgumentException("A CDN failure names at least one attempt.", nameof(attempts));
        Attempts = attempts;
    }
    public IReadOnlyList<CdnAttempt> Attempts { get; }
    public CdnFailure Failure => Attempts[^1].Failure;
    public string? Code => Attempts[^1].Code;
    public DateTimeOffset ObservedAt => Attempts[^1].ObservedAt;
    /// <summary>
    /// Offline only when no host answered at all. A passing failure on any host (no connection, timeout, 5xx, broken connection,
    /// an ignored or wrong range) wins over bad bytes or a wrong size from another host: one host's bad copy doesn't prove the
    /// key is bad while another host could still be asked. Otherwise bad bytes, then a size conflict. Hosts that answered 404
    /// or not at all make the dated 404 observation.
    /// </summary>
    public CdnFailure Overall =>
        Attempts.All(a => a.Failure == CdnFailure.Offline) ? CdnFailure.Offline :
        Attempts.LastOrDefault(a => Passing(a.Failure) && (a.Failure != CdnFailure.Offline || Attempts.Any(b => b.Failure is CdnFailure.VerificationFailed or CdnFailure.SizeConflict)))
            is { } passing ? passing.Failure :
        Attempts.Any(a => a.Failure == CdnFailure.VerificationFailed) ? CdnFailure.VerificationFailed :
        Attempts.Any(a => a.Failure == CdnFailure.SizeConflict) ? CdnFailure.SizeConflict :
        Attempts.All(a => a.Failure is CdnFailure.NotFound or CdnFailure.Offline) ? CdnFailure.NotFound :
        Failure;

    private static bool Passing(CdnFailure f) =>
        f is CdnFailure.Offline or CdnFailure.Timeout or CdnFailure.Network or CdnFailure.HttpStatus or CdnFailure.RangeIgnored or CdnFailure.RangeMismatch;
    /// <summary>Everything but a size conflict and a verification failure can be tried again later.</summary>
    public bool Retryable => Overall is not (CdnFailure.SizeConflict or CdnFailure.VerificationFailed or CdnFailure.Protocol or CdnFailure.TooLarge);
}
