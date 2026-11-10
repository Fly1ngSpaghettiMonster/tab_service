using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace TabService.Security;

public static class Tokens
{
    /// <summary>A new 256-bit random token, base64url encoded (URL and fragment safe).</summary>
    public static string New() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>Only hashes are stored, so a database leak does not expose working tokens.</summary>
    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    public static bool Matches(string? token, byte[] hash) =>
        !string.IsNullOrEmpty(token) && CryptographicOperations.FixedTimeEquals(Hash(token), hash);

    public static bool EqualsConstantTime(string? a, string b) =>
        a is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
