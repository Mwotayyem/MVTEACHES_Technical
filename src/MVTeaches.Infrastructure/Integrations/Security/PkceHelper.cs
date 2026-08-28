using System.Security.Cryptography;
using System.Text;

namespace MVTeaches.Infrastructure.Integrations.Security;

/// <summary>RFC 7636 PKCE — used for both Zoom's and Google's OAuth
/// authorization-code flows (owner clarification 2026-08-29: "Use PKCE where
/// the provider supports or requires it" — both do). S256 only; the "plain"
/// method is deliberately not offered.</summary>
public static class PkceHelper
{
    public static string NewCodeVerifier()
    {
        // 32 random bytes -> 43-character base64url string, within RFC 7636's
        // required 43-128 character verifier length.
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(bytes);
    }

    public static string ComputeCodeChallenge(string codeVerifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(hash);
    }

    public static string NewState()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
