using System.Security.Cryptography;
using System.Text;

namespace RummiHub.Web.Services;

/// <summary>
/// Per-seat bearer secret. Issued once, to the owning connection only, and required to
/// attach a new SignalR connection to that seat (page refresh, reconnect).
/// </summary>
public static class SeatSecrets
{
    public const int ByteLength = 32;

    public static string Create() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(ByteLength)).ToLowerInvariant();

    public static bool Matches(string? expected, string? presented)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(presented))
            return false;

        if (presented.Length != expected.Length || expected.Length > ByteLength * 2)
            return false;

        var a = Encoding.ASCII.GetBytes(expected);
        var b = Encoding.ASCII.GetBytes(presented);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
