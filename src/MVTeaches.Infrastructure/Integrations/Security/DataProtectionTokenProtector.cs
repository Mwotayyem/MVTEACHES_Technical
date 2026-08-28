using Microsoft.AspNetCore.DataProtection;
using MVTeaches.Application.Integrations;

namespace MVTeaches.Infrastructure.Integrations.Security;

/// <summary>
/// Owner clarification (2026-08-29): "Encrypt access and refresh tokens at
/// rest" / "Persist encryption/Data Protection keys securely outside the
/// application database so tokens remain decryptable after restarts and
/// deployments." ASP.NET Core Data Protection already provides authenticated
/// encryption and key rotation; the only thing this class adds is a single,
/// stable purpose string (so a key ring accidentally shared with another
/// Data-Protection consumer in the app can never cross-decrypt these tokens)
/// and a narrow interface so nothing outside this file ever touches
/// <see cref="IDataProtector"/> directly. The key ring's actual storage
/// location is configured once in Program.cs (PersistKeysToFileSystem, or
/// the production equivalent) — never inside this class.
/// </summary>
public class DataProtectionTokenProtector : ITokenProtector
{
    private const string Purpose = "MVTeaches.VideoMeetings.OAuthTokens.v1";

    private readonly IDataProtector _protector;

    public DataProtectionTokenProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    public string Protect(string plaintextToken) => _protector.Protect(plaintextToken);

    public string Unprotect(string protectedToken) => _protector.Unprotect(protectedToken);
}
