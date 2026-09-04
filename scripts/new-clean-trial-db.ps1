<#
.SYNOPSIS
    Creates a SEPARATE, empty "clean trial" database with nothing in it but the
    reference data and the bootstrap admin - so the whole journey can be walked
    from the beginning, as if the platform had just been handed over.

.DESCRIPTION
    Owner decision 2026-09-04: "a clean database as if we had just handed the
    project to the platform owner for the first time - only the main admin
    account. No students, no guardians, no teachers, no subscriptions, no
    payments, no sessions, no compensations, no old test data." And, in the
    same breath: "do not delete the current Local Staging database."

    This script is built so that it CANNOT do the second thing. It creates a
    new database and refuses to touch one that already exists:

      * It refuses outright if the target name is the Local Staging database.
      * It refuses if a database of the target name already exists, rather than
        emptying or reusing it. Pick another name with -DatabaseName.
      * There is no DROP, no TRUNCATE and no DELETE anywhere in this file. It
        cannot destroy data because it contains no statement that destroys
        data - which is a stronger guarantee than a script that could, but
        promises not to.

    What it actually does, in order:

      1. Reads Local Staging's own gitignored secrets file for the PostgreSQL
         host/port/user/password. Nothing is prompted for, nothing is echoed,
         and no credential is ever written to the console or to a log.
      2. Checks that the target database does not exist, and creates it.
      3. Applies every migration to it with `dotnet ef database update`.
      4. Writes appsettings.CleanTrial.secrets.json - the same mechanism Local
         Staging already uses, pointed at the new database, with the demo
         seeder explicitly disabled so the database stays genuinely empty of
         people.
      5. Reports the row counts it can see, so "empty" is something you read
         rather than something you take on trust.

    The application then fills in exactly two things on first start-up:
    DataSeeder's reference data (roles, countries, levels, age groups,
    settings, and the twenty-one courses) and the bootstrap admin. Nothing
    else, because StagingSeed:Enabled is false in the file it writes - and
    even if it were true, StagingSeeder independently refuses to run against a
    database whose name is not its configured RequiredDatabaseName.

.EXAMPLE
    .\scripts\new-clean-trial-db.ps1

.EXAMPLE
    # Create it and start it straight away, on its own ports so Local Staging
    # can keep running beside it.
    .\scripts\new-clean-trial-db.ps1 -Run

.EXAMPLE
    # A second, third, ... trial run. Each one is a new database; none of the
    # earlier ones is touched.
    .\scripts\new-clean-trial-db.ps1 -DatabaseName mvteaches_trial2 -Run
#>
param(
    [string]$DatabaseName = "mvteaches_cleantrial",
    [string]$StagingSecretsPath,
    [int]$HttpsPort = 7218,
    [int]$HttpPort = 5095,
    [switch]$Run
)

$ErrorActionPreference = "Stop"

$repoRoot      = Resolve-Path (Join-Path $PSScriptRoot "..")
$webProjectDir = Join-Path $repoRoot "src\MVTeaches.Web"

if (-not $StagingSecretsPath) {
    $StagingSecretsPath = Join-Path $webProjectDir "appsettings.Staging.secrets.json"
}

Write-Host "== Clean trial database ==" -ForegroundColor Cyan

if (-not (Test-Path $StagingSecretsPath)) {
    throw "Could not find $StagingSecretsPath. That gitignored file holds the PostgreSQL connection this script copies its credentials from - see docs/LOCAL-STAGING.md for how to create it."
}

$stagingSecrets = Get-Content -Raw -Path $StagingSecretsPath | ConvertFrom-Json
$stagingConnection = $stagingSecrets.ConnectionStrings.MvTeaches
if (-not $stagingConnection) {
    throw "$StagingSecretsPath has no ConnectionStrings:MvTeaches value to copy the server details from."
}

# Split "Key=Value;Key=Value" while remembering the original key spelling, so
# the connection string this script writes back looks like the one it read.
$parts = [ordered]@{}
foreach ($pair in $stagingConnection.Split(';')) {
    if ([string]::IsNullOrWhiteSpace($pair)) { continue }
    $index = $pair.IndexOf('=')
    if ($index -lt 1) { continue }
    $parts[$pair.Substring(0, $index).Trim()] = $pair.Substring($index + 1).Trim()
}

function Get-Part([string[]]$names) {
    foreach ($name in $names) {
        foreach ($key in $parts.Keys) {
            if ($key -ieq $name) { return $parts[$key] }
        }
    }
    return $null
}

function Get-PartKey([string[]]$names) {
    foreach ($name in $names) {
        foreach ($key in $parts.Keys) {
            if ($key -ieq $name) { return $key }
        }
    }
    return $null
}

$pgHost     = Get-Part @('Host', 'Server')
$pgPort     = Get-Part @('Port')
$pgUser     = Get-Part @('Username', 'User ID', 'UserId', 'User')
$pgPassword = Get-Part @('Password')
$stagingDb  = Get-Part @('Database')
$databaseKey = Get-PartKey @('Database')

if (-not $pgHost) { $pgHost = "127.0.0.1" }
if (-not $pgPort) { $pgPort = "5432" }
if (-not $stagingDb -or -not $databaseKey) {
    throw "The connection string in $StagingSecretsPath names no Database, so this script cannot tell which database it must avoid. Refusing to continue."
}

# Guard 1. The whole point of this script is that the existing database is
# left alone; naming it here would defeat that in one keystroke.
if ($DatabaseName -ieq $stagingDb) {
    throw "-DatabaseName is the Local Staging database itself. This script only ever creates a NEW, separate database - it will not migrate, empty, or reuse that one. Choose a different name."
}

Write-Host "Target database : $DatabaseName"
Write-Host "Server          : $pgHost`:$pgPort"
Write-Host "Untouched       : $stagingDb (Local Staging)" -ForegroundColor Green

$psql = Get-Command psql -ErrorAction SilentlyContinue
if (-not $psql) {
    throw @"
psql was not found on PATH, and without it this script cannot verify that '$DatabaseName' does not already exist. It will not create a database it cannot first prove is new.

Either add PostgreSQL's bin folder to PATH, or check by hand and create it yourself:

    SELECT 1 FROM pg_database WHERE datname = '$DatabaseName';   -- must return nothing
    CREATE DATABASE "$DatabaseName";

then re-run this script - it will see the database, skip creation, and carry on with the migrations and the config file.
"@
}

# PGPASSWORD is passed to psql through the environment, never on the command
# line, so it cannot appear in the console, in a log, or in the process list.
$previousPgPassword = $env:PGPASSWORD
$env:PGPASSWORD = $pgPassword
try {
    $exists = & psql -h $pgHost -p $pgPort -U $pgUser -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = '$DatabaseName'"
    if ($LASTEXITCODE -ne 0) {
        throw "Could not query PostgreSQL on $pgHost`:$pgPort as '$pgUser'. Is the server running, and are the credentials in $StagingSecretsPath still valid?"
    }

    if ("$exists".Trim() -eq "1") {
        # Guard 2. Deliberately a refusal and not a prompt: a database that
        # already exists may be somebody's trial run in progress, and this
        # script has no way to know. Creating a differently-named one costs
        # nothing; emptying the wrong one cannot be undone.
        Write-Host "Database '$DatabaseName' already exists." -ForegroundColor Yellow
        Write-Host "Nothing has been changed. This script never reuses or empties an existing database." -ForegroundColor Yellow
        throw "Re-run with a name that is free, e.g. -DatabaseName ${DatabaseName}2."
    }

    Write-Host "Creating database '$DatabaseName' ..."
    & psql -h $pgHost -p $pgPort -U $pgUser -d postgres -c "CREATE DATABASE ""$DatabaseName"";" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "CREATE DATABASE failed - see the psql output above."
    }
}
finally {
    $env:PGPASSWORD = $previousPgPassword
}

# ---------------------------------------------------------------- migrations
$parts[$databaseKey] = $DatabaseName
$trialConnection = ($parts.Keys | ForEach-Object { "$_=$($parts[$_])" }) -join ';'

Write-Host "Applying migrations ..."
$previousEnvironment = $env:ASPNETCORE_ENVIRONMENT
$previousSecretsPath = $env:MVTEACHES_STAGING_SECRETS_PATH
$previousConnection  = $env:ConnectionStrings__MvTeaches
try {
    # Program.cs adds the Staging secrets file AFTER the environment-variable
    # provider, so under ASPNETCORE_ENVIRONMENT=Staging that file would outrank
    # the variable below and these migrations would land on Local Staging's
    # database instead. Both are neutralised here explicitly rather than
    # assumed to be unset, because this shell may have run something else first.
    $env:ASPNETCORE_ENVIRONMENT = "Development"
    $env:MVTEACHES_STAGING_SECRETS_PATH = $null
    $env:ConnectionStrings__MvTeaches = $trialConnection

    dotnet ef database update `
        --project (Join-Path $repoRoot "src\MVTeaches.Infrastructure") `
        --startup-project (Join-Path $repoRoot "src\MVTeaches.Web")
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet ef database update failed (exit code $LASTEXITCODE)."
    }
}
finally {
    $env:ASPNETCORE_ENVIRONMENT = $previousEnvironment
    $env:MVTEACHES_STAGING_SECRETS_PATH = $previousSecretsPath
    $env:ConnectionStrings__MvTeaches = $previousConnection
}

# --------------------------------------------------------------- config file
$trialSecretsPath = Join-Path $webProjectDir "appsettings.CleanTrial.secrets.json"

$bootstrap = $stagingSecrets.Bootstrap
$hasBootstrap = $bootstrap -and $bootstrap.AdminEmail -and $bootstrap.AdminPassword

$trialSettings = [ordered]@{
    ConnectionStrings = [ordered]@{ MvTeaches = $trialConnection }
    # Explicitly false, not merely absent. This is what keeps the trial
    # database empty of people: everything StagingSeeder creates is demo data,
    # and the point of this exercise is to have none of it. StagingSeeder also
    # refuses independently, because RequiredDatabaseName will not match.
    StagingSeed       = [ordered]@{ Enabled = $false; RequiredDatabaseName = $stagingDb }
}

if ($hasBootstrap) {
    # Copied, never displayed. These are the same values already sitting in the
    # Local Staging secrets file on this machine, so nothing new is being
    # written down that was not already here - and both files are gitignored.
    $trialSettings.Bootstrap = [ordered]@{
        AdminEmail    = $bootstrap.AdminEmail
        AdminPassword = $bootstrap.AdminPassword
    }
}

$trialSettings | ConvertTo-Json -Depth 5 | Set-Content -Path $trialSecretsPath -Encoding utf8
Write-Host "Wrote $trialSecretsPath (gitignored)."

if (-not $hasBootstrap) {
    Write-Host ""
    Write-Host "No Bootstrap:AdminEmail/AdminPassword was found in the Local Staging secrets file, so none was copied." -ForegroundColor Yellow
    Write-Host "Add a Bootstrap section to $trialSecretsPath before starting, or the trial database will have no way to sign in." -ForegroundColor Yellow
    Write-Host "The admin is created on first start-up only while the Admin role has no members, and the password is never printed by anything." -ForegroundColor Yellow
}

# ------------------------------------------------------------------- report
$previousPgPassword = $env:PGPASSWORD
$env:PGPASSWORD = $pgPassword
try {
    $counts = & psql -h $pgHost -p $pgPort -U $pgUser -d $DatabaseName -tAc @"
SELECT 'students=' || (SELECT COUNT(*) FROM students)
    || ' guardians=' || (SELECT COUNT(*) FROM guardians)
    || ' teachers=' || (SELECT COUNT(*) FROM teachers)
    || ' subscriptions=' || (SELECT COUNT(*) FROM subscriptions)
    || ' payments=' || (SELECT COUNT(*) FROM payments)
    || ' sessions=' || (SELECT COUNT(*) FROM class_sessions)
    || ' courses=' || (SELECT COUNT(*) FROM courses)
    || ' levels=' || (SELECT COUNT(*) FROM levels);
"@
    if ($LASTEXITCODE -eq 0) {
        Write-Host ""
        Write-Host "Contents of ${DatabaseName}: $("$counts".Trim())" -ForegroundColor Green
        Write-Host "(courses and levels fill in on first start-up, from DataSeeder; everything else must stay at 0 until you create it yourself.)"
    }
}
finally {
    $env:PGPASSWORD = $previousPgPassword
}

Write-Host ""
Write-Host "Done. Local Staging's database '$stagingDb' was not opened, migrated, or modified." -ForegroundColor Green

if ($Run) {
    $trialAppData = Join-Path $webProjectDir "App_Data\cleantrial"
    Write-Host ""
    Write-Host "Starting the clean trial run on https://localhost:$HttpsPort ..." -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot "run-local-staging.ps1") `
        -SecretsPath $trialSecretsPath `
        -AppDataDir $trialAppData `
        -HttpsPort $HttpsPort `
        -HttpPort $HttpPort `
        -PublishDir (Join-Path $webProjectDir "bin\CleanTrialPublish")
}
else {
    Write-Host ""
    Write-Host "To start it (Local Staging can keep running on 7217 at the same time):"
    Write-Host "    .\scripts\run-local-staging.ps1 -SecretsPath `"$trialSecretsPath`" -AppDataDir `"$(Join-Path $webProjectDir 'App_Data\cleantrial')`" -HttpsPort $HttpsPort -HttpPort $HttpPort -PublishDir `"$(Join-Path $webProjectDir 'bin\CleanTrialPublish')`""
    Write-Host "or simply re-run this script with -Run."
}
