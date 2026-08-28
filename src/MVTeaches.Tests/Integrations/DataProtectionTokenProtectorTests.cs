using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using MVTeaches.Application.Integrations;
using MVTeaches.Infrastructure.Integrations.Security;
using Xunit;

namespace MVTeaches.Tests.Integrations;

/// <summary>
/// Owner clarification (2026-08-29): "Encrypt access and refresh tokens at
/// rest" and "Persist encryption/Data Protection keys securely outside the
/// application database so tokens remain decryptable after restarts and
/// deployments."
///
/// The second half is the one that silently breaks a deployment: with the
/// default in-memory/ephemeral key ring, every restart makes every stored
/// token permanently undecryptable and every teacher has to reconnect. These
/// tests use two INDEPENDENT service providers over the same on-disk key
/// directory — the closest faithful stand-in for "the app restarted" — to
/// prove the persisted-key configuration actually survives that.
/// </summary>
public class DataProtectionTokenProtectorTests : IDisposable
{
    private readonly string _keyDirectory =
        Path.Combine(Path.GetTempPath(), "mvteaches-dp-tests", Guid.NewGuid().ToString("N"));

    private ITokenProtector CreateProtectorAsIfFreshlyStarted()
    {
        // Mirrors Program.cs exactly: a stable application name plus a
        // file-system key ring outside the database.
        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("MVTeaches")
            .PersistKeysToFileSystem(new DirectoryInfo(_keyDirectory));
        var provider = services.BuildServiceProvider();
        return new DataProtectionTokenProtector(provider.GetRequiredService<IDataProtectionProvider>());
    }

    [Fact]
    public void A_protected_token_round_trips_back_to_the_original_value()
    {
        var protector = CreateProtectorAsIfFreshlyStarted();

        var protectedValue = protector.Protect("refresh-token-abc123");

        Assert.Equal("refresh-token-abc123", protector.Unprotect(protectedValue));
    }

    [Fact]
    public void A_protected_token_never_contains_the_plaintext()
    {
        var protector = CreateProtectorAsIfFreshlyStarted();

        var protectedValue = protector.Protect("SUPER-SECRET-REFRESH-TOKEN");

        Assert.DoesNotContain("SUPER-SECRET-REFRESH-TOKEN", protectedValue, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", protectedValue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_token_protected_before_a_restart_is_still_decryptable_after_it()
    {
        var beforeRestart = CreateProtectorAsIfFreshlyStarted();
        var protectedValue = beforeRestart.Protect("survives-the-deployment");

        // A completely separate DI container reading the same persisted key
        // ring — the whole point of PersistKeysToFileSystem.
        var afterRestart = CreateProtectorAsIfFreshlyStarted();

        Assert.Equal("survives-the-deployment", afterRestart.Unprotect(protectedValue));
    }

    [Fact]
    public void A_token_from_a_different_key_ring_cannot_be_decrypted()
    {
        var ours = CreateProtectorAsIfFreshlyStarted();

        var foreignServices = new ServiceCollection();
        foreignServices.AddDataProtection()
            .SetApplicationName("MVTeaches")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_keyDirectory, "..", Guid.NewGuid().ToString("N"))));
        var foreign = new DataProtectionTokenProtector(
            foreignServices.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>());

        var protectedByForeign = foreign.Protect("not-ours");

        Assert.ThrowsAny<Exception>(() => ours.Unprotect(protectedByForeign));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_keyDirectory))
            {
                Directory.Delete(_keyDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }
}
