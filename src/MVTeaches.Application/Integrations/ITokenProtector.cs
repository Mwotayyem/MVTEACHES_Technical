namespace MVTeaches.Application.Integrations;

/// <summary>
/// The one seam every OAuth access/refresh token passes through before it
/// touches the database or leaves it. Owner clarification (2026-08-29):
/// "Encrypt access and refresh tokens at rest" and "Persist encryption/Data
/// Protection keys securely outside the application database so tokens
/// remain decryptable after restarts and deployments" — the Infrastructure
/// implementation wraps ASP.NET Core Data Protection with a dedicated
/// purpose string and a persisted key ring (see Program.cs), so this
/// interface itself carries no key-management concern.
/// </summary>
public interface ITokenProtector
{
    string Protect(string plaintextToken);

    string Unprotect(string protectedToken);
}
