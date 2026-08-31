<#
.SYNOPSIS
    Publishes and runs Local Staging the only way that actually renders
    correctly: `dotnet publish` first, then run the published output -
    never `dotnet run` directly.

.DESCRIPTION
    `dotnet run`/`dotnet build` never generate the precompressed `.br`/`.gz`
    sibling files that ASP.NET Core's static-asset compression negotiation
    requires outside the Development environment. Without them, Staging
    serves every CSS/JS file as HTTP 200 with an EMPTY body to any real
    browser (which always sends `Accept-Encoding: br`) - confirmed by
    direct browser testing, not just PowerShell/curl checks that don't send
    that header by default. `dotnet publish` is the only step that creates
    those files, so this script always publishes fresh before running.

    The published DLL is started FROM the publish output folder (its
    working directory is left as the publish folder, not changed) -
    ASP.NET Core resolves its content root from the current directory by
    default, and the precompressed .br/.gz files that fix the bug above
    only exist under the PUBLISHED wwwroot, not the source one. An earlier
    version of this script called Push-Location on the source project
    folder before running the DLL to keep DataProtectionKeysPath/
    FileStorage:StoragePath resolving to a persistent App_Data folder -
    that broke static assets again (content root silently became the
    source tree, which has no precompressed files), so persistence is
    handled here instead by passing DataProtectionKeysPath and
    FileStorage__StoragePath as absolute-path environment variables
    pointing at the source project's App_Data folder, overriding the
    relative defaults in appsettings.Staging.json without touching the
    working directory. The publish output itself lives under bin\, which
    is already gitignored, so nothing new needs to be excluded from Git
    for this.

.EXAMPLE
    .\scripts\run-local-staging.ps1

.EXAMPLE
    # Publish somewhere else, e.g. to inspect the output by hand:
    .\scripts\run-local-staging.ps1 -PublishDir C:\Temp\mvteaches-staging-publish
#>
param(
    [string]$PublishDir
)

$ErrorActionPreference = "Stop"

$repoRoot      = Resolve-Path (Join-Path $PSScriptRoot "..")
$webProjectDir = Join-Path $repoRoot "src\MVTeaches.Web"
$webProject    = Join-Path $webProjectDir "MVTeaches.Web.csproj"

if (-not $PublishDir) {
    $PublishDir = Join-Path $webProjectDir "bin\LocalStagingPublish"
}

Write-Host "== Local Staging ==" -ForegroundColor Cyan
Write-Host "Publishing (Release) to: $PublishDir"
dotnet publish $webProject -c Release -o $PublishDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed (exit code $LASTEXITCODE) - not starting a stale build."
}

$dll = Join-Path $PublishDir "MVTeaches.Web.dll"
if (-not (Test-Path $dll)) {
    throw "Publish succeeded but $dll was not found - check the publish output above."
}

$appDataDir = Join-Path $webProjectDir "App_Data\staging"

Write-Host "Starting from the published build (Staging environment) ..."
Write-Host "  https://localhost:7217"
Write-Host "  http://localhost:5094"
Write-Host "Running from: $PublishDir (content root stays here so precompressed .br/.gz assets resolve)"
Write-Host "Persistent data: $appDataDir"
Write-Host "Press Ctrl+C to stop." -ForegroundColor Yellow

$env:ASPNETCORE_ENVIRONMENT = "Staging"
$env:ASPNETCORE_URLS = "https://localhost:7217;http://localhost:5094"
# Absolute overrides so Data Protection keys and uploaded receipts persist in
# the source project's App_Data folder across every publish, instead of
# following the relative paths in appsettings.Staging.json (which would
# otherwise resolve against the publish folder and be wiped on every publish).
$env:DataProtectionKeysPath = Join-Path $appDataDir "dataprotection-keys"
$env:FileStorage__StoragePath = Join-Path $appDataDir "private-uploads"
# Same reasoning: appsettings.Staging.secrets.json is deliberately excluded
# from publish output (see the .csproj), so a relative path in Program.cs
# would resolve against the publish folder (this script's working
# directory) and never find it. Point it at this absolute, stable location
# instead — the project folder, where the file actually lives.
$env:MVTEACHES_STAGING_SECRETS_PATH = Join-Path $webProjectDir "appsettings.Staging.secrets.json"

Push-Location $PublishDir
try {
    & dotnet $dll
}
finally {
    Pop-Location
}
