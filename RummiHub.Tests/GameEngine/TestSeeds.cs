using System.Security.Cryptography;
using System.Text;

namespace RummiHub.Tests.GameEngineTests;

/// <summary>Readable names for test seeds: each name gives its own fixed 64-hex seed.</summary>
internal static class TestSeeds
{
    public static string Of(string name) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name))).ToLowerInvariant();
}
