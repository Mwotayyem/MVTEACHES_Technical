using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MVTeaches.Tests;

/// <summary>
/// Builds a real, resx-backed <see cref="IStringLocalizer{T}"/> for services
/// under test that need one, without spinning up a full ASP.NET Core host —
/// the same <see cref="ResourceManagerStringLocalizerFactory"/> that
/// <c>AddLocalization()</c> wires up in the running app, constructed
/// directly. Falls back to each resx's neutral (English) values whenever the
/// test host's own <see cref="System.Globalization.CultureInfo.CurrentUICulture"/>
/// isn't one of the app's supported cultures — exactly like production.
/// </summary>
internal static class TestLocalization
{
    public static IStringLocalizer<T> For<T>() where T : class
    {
        var factory = new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance);
        return new StringLocalizer<T>(factory);
    }
}
