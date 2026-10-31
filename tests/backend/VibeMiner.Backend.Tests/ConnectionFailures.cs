using System.Net.Sockets;
using System.Security.Authentication;

namespace VibeMiner.Backend.Tests;

/// <summary>
/// The shapes .NET 10 gives a connection, direct or to a proxy, that ends before any answer, and two that aren't that
/// (#125, #127). Offline: reset, ended, tls-reset. Failed: certificate, garbled.
/// </summary>
internal static class ConnectionFailures
{
    public static HttpRequestException Of(string shape)
    {
        var reset = new IOException("Unable to read data from the transport connection: Connection reset by peer.",
            new SocketException((int)SocketError.ConnectionReset));
        return shape switch
        {
            "reset" => new(HttpRequestError.Unknown, "An error occurred while sending the request.", reset),
            "ended" => new(HttpRequestError.ResponseEnded, "The response ended prematurely.", new HttpIOException(HttpRequestError.ResponseEnded)),
            "tls-reset" => new(HttpRequestError.SecureConnectionError, "The SSL connection could not be established.", reset),
            "certificate" => new(HttpRequestError.SecureConnectionError, "The SSL connection could not be established.",
                new AuthenticationException("The remote certificate is invalid.")),
            "garbled" => new(HttpRequestError.InvalidResponse, "The server returned an invalid or unrecognized response.",
                new HttpIOException(HttpRequestError.InvalidResponse)),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
        };
    }
}
