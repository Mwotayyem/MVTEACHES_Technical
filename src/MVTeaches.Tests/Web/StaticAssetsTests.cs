using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace MVTeaches.Tests.Web;

/// <summary>
/// Regression coverage for a real bug found while diagnosing Local Staging:
/// running via `dotnet build`/`dotnet run` (as opposed to `dotnet publish`,
/// or Visual Studio's own build pipeline) can leave a page's referenced
/// CSS/JS assets unresolved on disk — either missing outright (a gitignored
/// vendor folder like wwwroot/lib that a fresh worktree never populated) or
/// present only as a build artifact the static-assets pipeline doesn't
/// expect at the physical path it checks (the RazorSDK scoped-CSS bundle).
/// Both failure modes are silent to a quick glance: the browser still gets
/// a 200/404/500 page, just without the intended stylesheet or script, or —
/// worse — an opaque friendly error page in any non-Development environment.
/// This test proves every asset the homepage actually references resolves
/// to its own real content, not an HTML fallback or a redirect.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class StaticAssetsTests : IClassFixture<StaticAssetsTests.Factory>
{
    private static readonly Regex AssetReferencePattern = new(
        "(?:href|src)=\"(/[^\"]+\\.(?:css|js))\"");

    private readonly Factory _factory;

    public StaticAssetsTests(TestDatabaseFixture fixture, Factory factory)
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__MvTeaches", fixture.ConnectionString);
        _factory = factory;
    }

    [Fact]
    public async Task Homepage_static_assets_return_their_own_content_not_html_or_redirect()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var home = await client.GetStringAsync("/");

        var assetPaths = AssetReferencePattern.Matches(home)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        Assert.NotEmpty(assetPaths);

        foreach (var path in assetPaths)
        {
            var response = await client.GetAsync(path);
            var contentType = response.Content.Headers.ContentType?.MediaType;
            var body = await response.Content.ReadAsStringAsync();

            Assert.True(
                response.StatusCode == HttpStatusCode.OK,
                $"{path}: expected 200 OK but got {(int)response.StatusCode} {response.StatusCode}.");
            Assert.True(
                contentType is "text/css" or "text/javascript",
                $"{path}: expected Content-Type text/css or text/javascript but got '{contentType}'.");
            Assert.False(
                body.TrimStart().StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase),
                $"{path}: response body is an HTML page, not the expected asset — likely an error/redirect fallback.");
        }
    }

    public class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseEnvironment("Development");
    }
}
