namespace VibeMiner.Infrastructure;

/// <summary>How the outside sources (WoWDBDefs, wow-listfile, wago.tools) tell an unreachable host from a failed fetch.</summary>
internal static class HttpFailures
{
    /// <summary>
    /// No answer came back: the name didn't resolve, no connection opened, or the connection, also one to a proxy, was
    /// closed or reset before any response (#125, #127). The pool reports a reset as an unknown error over a socket
    /// error. A certificate failure or an answer VibeMiner can't read is a failure, not offline.
    /// </summary>
    public static bool Unreachable(HttpRequestException error) =>
        error.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded
        || HasSocketError(error.InnerException);

    private static bool HasSocketError(Exception? error)
    {
        for (var depth = 0; error is not null && depth < 8; error = error.InnerException, depth++)
            if (error is System.Net.Sockets.SocketException) return true;
        return false;
    }
}
