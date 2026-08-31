# Local Staging — an isolated, repeatable acceptance-testing environment

This describes **Local Staging**: a second, fully isolated copy of the real
application running on this same machine, in the `Staging` ASP.NET Core
environment, against its own PostgreSQL database (`mvteaches_staging`) —
built specifically so the real system (real services, real database, real
authorization rules) can be exercised end to end without touching
Development's own data (`mvteaches_local`) and without deploying anywhere.

This is **not** a real remote staging/production deployment — see
`/docs/deployment/README.md` for that (manual migrations, environment
variables on a VPS, no auto-seeding). Local Staging is a convenience layer
on top of the same code, for this machine only.

## 1. Where this lives

- **Git**: branch `staging-setup`, branched from `backup/pre-staging-2026-08-31`
  (the commit that holds the full localization + payments work, saved and
  pushed with the owner's explicit approval before this task began).
- **Filesystem**: a **separate git worktree** at
  `عون-staging-setup` (sibling to the main `عون` checkout), so Development's
  own working folder — still on `Mwotayyem-patch-1` — is never touched by
  anything in this document. Run `git worktree list` from either folder to
  see both.

## 2. What's different from Development — at a glance

| | Development | Local Staging |
|---|---|---|
| ASP.NET Core environment | `Development` | `Staging` |
| Launch profile | `https` (or `http`) | **`Local Staging`** |
| Ports | `https://localhost:7216`, `http://localhost:5093` | `https://localhost:7217`, `http://localhost:5094` |
| Database | `mvteaches_local` (port 5432) | `mvteaches_staging` (port 5432, **separate role + database**) |
| Secrets source | `dotnet user-secrets` (auto-loaded because `IsDevelopment()`) | **`appsettings.Staging.secrets.json`** (gitignored, next to `appsettings.Staging.json`) — Staging does NOT get User Secrets automatically; see §4 |
| Launch method | `dotnet run` / F5 in Visual Studio | **`dotnet publish` then run the published output** — `dotnet run` is NOT sufficient for Staging; see §3.3 |
| Auth cookie name | ASP.NET Core Identity's own default | `.MVTeaches.Identity.Staging` (environment-suffixed, never collides with Development's in the same browser) |
| Antiforgery cookie name | ASP.NET Core's own default | `.MVTeaches.Antiforgery.Staging` |
| Data Protection keys | `bin/dataprotection-keys` (next to the binaries) | `App_Data/staging/dataprotection-keys` (persistent, survives rebuilds, never committed — see `.gitignore`) |
| Uploaded receipts | `bin/private-uploads` | `App_Data/staging/private-uploads` |
| On-screen banner | none | Orange bar, Arabic + English: "بيانات تجريبية — Local Staging — ليست بيانات حقيقية · Test data — not real data" |
| Auto-migrate + auto-seed on startup | `LocalDevelopmentSeed:Enabled` (Development-only guard) | `StagingSeed:Enabled` (**wholly separate class/guard**, Staging-only — see `StagingSeeder.cs`) |
| Business rules (payment/booking/level/payroll) | Identical code | Identical code — nothing here changes any rule |

No secrets appear in this table or in any committed file — see §4.

## 3. One-time setup

### 3.1 Create the database and its own role

Run once, as the local Postgres superuser (adjust host/port to your own
instance):

```sql
CREATE ROLE mvteaches_staging WITH LOGIN PASSWORD 'choose-a-real-password';
CREATE DATABASE mvteaches_staging OWNER mvteaches_staging;
```

This is a genuinely separate role and database from `mvteaches_local` —
inspecting or dropping one can never accidentally touch the other.

### 3.2 Create `appsettings.Staging.secrets.json` (this machine only, never committed)

**Do not use a machine-wide `'User'`-scope environment variable for
this.** That was tried once and it silently redirected *every* `dotnet`
process on this Windows account — including a plain Development run — to
`mvteaches_staging`, with no warning at all. It was removed for exactly
this reason.

Instead, create `src/MVTeaches.Web/appsettings.Staging.secrets.json`
(gitignored — see `.gitignore` — and loaded by `Program.cs` only when
`builder.Environment.IsStaging()` is true, so it can never be read by a
Development or Production run even if it ended up on the wrong machine).

**This file's location is resolved from `MVTEACHES_STAGING_SECRETS_PATH`
(an absolute path), not a fixed relative one** — `scripts/run-local-staging.ps1`
sets that variable to this project folder before launching. This matters
because the file is deliberately excluded from `dotnet publish` output
(see the `.csproj`), and the published app's working directory is the
publish folder itself (§3.3) — a relative path would silently resolve
there, find nothing, and every setting below would become a no-op with no
error (this was hit once: `StagingSeed:SeedPassword`/`Bootstrap:AdminPassword`
changes had zero effect until this was fixed, because the connection
string kept working only by coincidence via an unrelated leftover
environment variable). A plain `dotnet run`/F5 launch needs no override —
its working directory is already the project folder, which is the
relative fallback.

```json
{
  "ConnectionStrings": {
    "MvTeaches": "Host=127.0.0.1;Port=5432;Database=mvteaches_staging;Username=mvteaches_staging;Password=<the password you chose>"
  },
  "Bootstrap": {
    "AdminEmail": "staging-admin@staging.mvteaches.local",
    "AdminPassword": "<a real password, 10+ chars, incl. one non-alphanumeric>"
  },
  "StagingSeed": {
    "Enabled": true,
    "SeedPassword": "<a real password, same rule>"
  }
}
```

**Password rule that actually matters here**: this app's Identity policy
(`Program.cs`) requires length ≥ 10 and — via ASP.NET Core Identity's own
unchanged default — at least one uppercase, one lowercase, one digit, and
one **non-alphanumeric** character. A purely alphanumeric password fails
silently loud (a clear error in the log, not a crash) — this was hit and
fixed once already while setting this environment up.

Remove `Bootstrap.AdminEmail`/`Bootstrap.AdminPassword` from the file once
the admin account exists (same one-time-only rule as Development's own
bootstrap; `StagingSeeder` reuses the *existing* admin from then on and
does not re-run this step). Keep `ConnectionStrings.MvTeaches` and
`StagingSeed.SeedPassword` in the file permanently — they're needed on
every run.

### 3.3 First run — and every run after: `dotnet run` is NOT enough

**Local Staging must be started via `dotnet publish` and then running the
published output — never `dotnet run` / F5's `Local Staging` profile
directly.** This was discovered the hard way: `dotnet run`/`dotnet build`
never generate the precompressed `.br`/`.gz` sibling files that ASP.NET
Core's static-asset compression negotiation requires outside the
Development environment. Without them, every CSS/JS request in Staging
still returns HTTP 200 with the right `Content-Type` — but with an
**empty body** — to any real browser, because a real browser always sends
`Accept-Encoding: br` (a plain PowerShell `Invoke-WebRequest` doesn't send
that header by default, which is why an earlier HTTP-only check missed
this entirely and reported "200 OK" while the page rendered as unstyled
raw HTML). Development is unaffected — it uses a different, dev-only
static-asset code path that doesn't depend on those precompressed files —
which is also why this never showed up when testing the base app.
`StaticAssetsTests.cs` (see `src/MVTeaches.Tests/Web/`) is an in-process
HTTP smoke test (200 + correct Content-Type + not an HTML/redirect body)
and does **not** cover this specific bug — it runs against a `dotnet
build` output under the Development environment, which never reproduces
the compression-negotiation issue described here. The manual check below
is the real verification for that.

The supported way to run Local Staging:

```powershell
cd عون-staging-setup
.\scripts\run-local-staging.ps1
```

This publishes fresh (Release) into `src/MVTeaches.Web/bin/LocalStagingPublish`
(already gitignored, since it's under `bin/`) and then runs that published
`MVTeaches.Web.dll` with `ASPNETCORE_ENVIRONMENT=Staging` on the usual
`https://localhost:7217` / `http://localhost:5094`, run **from the publish
folder itself** — not from the `MVTeaches.Web` project folder. This matters:
an earlier version of this script changed the working directory to the
project folder so `App_Data` paths would stay persistent, and that broke
static assets all over again, because ASP.NET Core resolves its content
root (and therefore where it looks for the precompressed `.br`/`.gz` files
from §3.3 above) from the current directory by default — pointing it back
at the source tree silently made it serve the *un*compressed source
`wwwroot`, which still returned the same empty bodies. Persistence of
`App_Data` (Data Protection keys, uploaded receipts — see §2) is handled
instead by the script setting `DataProtectionKeysPath` and
`FileStorage__StoragePath` as absolute-path environment variables pointing
at the project folder's `App_Data/staging`, which override the relative
defaults in `appsettings.Staging.json` without moving the working
directory. To link it into Visual Studio 2026 as an
External Tool: **Tools → External Tools → Add**, Command =
`powershell.exe`, Arguments =
`-ExecutionPolicy Bypass -File "$(SolutionDir)..\scripts\run-local-staging.ps1"`,
Initial directory = `$(SolutionDir)..`.

**Verifying the design actually rendered — HTTP 200 alone proves
nothing.** After running the script, open `https://localhost:7217` in a
real browser (not `Invoke-WebRequest`/`curl` without an explicit
`Accept-Encoding` header) and hard-refresh. This manual browser check, or
an explicit `curl -H "Accept-Encoding: br"` against a CSS/JS URL
confirming a non-empty body, is the real verification — `StaticAssetsTests`
does not cover this (see above).

On first run this: connects, refuses outright if the connected database's
name isn't exactly `mvteaches_staging` (the same defence-in-depth pattern
`LocalDevelopmentSeeder` already uses for Development), applies every
migration from empty, seeds the always-on reference data (roles, age
groups, countries, levels, course, settings — same as every environment),
creates the bootstrap admin, and then seeds the Local Staging test
accounts (§5). All of this is logged under the `MVTeaches.StagingSeed`
category. Every step is idempotent — a second run only reconciles
passwords that no longer match, never resets on every run, and never
duplicates a row.

## 4. Secrets — how they're handled, never printed

- `appsettings.Staging.json` is **committed** and contains **zero**
  secrets — only non-secret defaults (`DataProtectionKeysPath`,
  `FileStorage:StoragePath`, `StagingSeed:RequiredDatabaseName`) and
  explanatory `_comment_*` keys.
- The database connection string, `Bootstrap.AdminPassword`, and
  `StagingSeed.SeedPassword` all live **only** in
  `src/MVTeaches.Web/appsettings.Staging.secrets.json`, created once per
  §3.2 — a file this repository's `.gitignore` excludes by exact name, and
  which `Program.cs` loads only when `IsStaging()` is true. It is never
  written to any file this repository tracks, never logged (the app logs
  "reconciled the configured seed password for X", never the value
  itself), never published (the `.csproj` excludes it by name from
  `dotnet publish` output — verify with `Get-ChildItem -Recurse
  <publish-folder> -Filter appsettings.Staging.secrets.json`, which should
  return nothing), and is not reproduced anywhere in this document either.
- To find or change a value later, open the file directly — it's a plain
  local JSON file, not an environment variable.
- **Do not** go back to a machine-wide environment variable for any of
  this — see §3.2 for why that was tried once and rejected.

## 5. Seeded Local Staging accounts

All emails use the `@staging.mvteaches.local` domain (distinct from
Development's `@mvteaches.local`) so the two environments' test accounts
can never be confused. All names carry a `[STAGING TEST DATA]` marker.
Passwords are whatever you set in §3.2 — never printed here.

| Email | Role | Password source | Notes |
|---|---|---|---|
| *(your `Bootstrap.AdminEmail`)* | Admin + SystemAdmin | `Bootstrap.AdminPassword` | Same production-safe bootstrap mechanism as every environment; `DataSeeder` grants it only while the Admin role has zero members. |
| `staging-teacher@staging.mvteaches.local` | Teacher | `StagingSeed.SeedPassword` | Granted the A1 level via the real, audited `ITeacherLevelAuthorizationService.GrantAsync` — not a raw insert. **No** Zoom/Google connection (deliberately — see §7). |
| `staging-guardian@staging.mvteaches.local` | Guardian | same | Linked to **two independent** children, "[STAGING TEST DATA] Child One" and "...Child Two" — neither has a login of their own or a placement result yet, so guardian-side isolation (acceptance step 9) can be exercised directly. |
| `staging-student@staging.mvteaches.local` | Student | same | A **direct** login, no placement result yet — demonstrates the "no result ⟹ purchase CTA, not a package list" rule from a cold start. |

Also seeded (visible once signed in, not logins themselves):

- One active `PaymentMethodConfig` (CliQ, JOD) — the only method the
  self-service purchase/transfer flow will offer.
- Published **A1 Group** and **A1 Private** pricing plans.
- A `[STAGING TEST DATA]` published, active placement test — two trivial
  questions, every possible score maps to A1 (deterministic outcome
  regardless of which answer is picked). Clearly-marked technical fixture
  content, never real academic material, per the standing rule against
  inventing exam content.
- Two future `ClassSession` rows for the seeded teacher (one Group, one
  Private) — seeded directly (bypassing the teacher-publish flow, which
  would correctly refuse a teacher with no video connection), specifically
  so booking can be tested without a real Zoom/Google account first.

## 6. Inspecting the database in pgAdmin — safe, read-only queries

Connect pgAdmin to `mvteaches_staging` (same server, its own login). Useful
read-only checks:

```sql
-- Every seeded staging account and its role
SELECT u."Email", r."Name" AS role
FROM "AspNetUsers" u
JOIN "AspNetUserRoles" ur ON ur."UserId" = u."Id"
JOIN "AspNetRoles" r ON r."Id" = ur."RoleId"
WHERE u."Email" LIKE '%@staging.mvteaches.local'
ORDER BY u."Email";

-- Guardian -> children isolation check
SELECT g.full_name AS guardian, s.full_name AS child, gs.relationship, gs.is_primary
FROM guardianships gs
JOIN guardians g ON g."Id" = gs."GuardianId"
JOIN students s ON s."Id" = gs."StudentId"
WHERE g.full_name LIKE '[STAGING TEST DATA]%';

-- Teacher's authorized levels
SELECT t.full_name, l.code
FROM teacher_level_assignments tla
JOIN teachers t ON t."Id" = tla.teacher_id
JOIN levels l ON l."Id" = tla.level_id
WHERE t.full_name LIKE '[STAGING TEST DATA]%';

-- Active payment methods offered to payers
SELECT "Id", type, beneficiary_name, accepted_currencies_csv, is_active FROM payment_method_configs;

-- A student's full subscription/payment picture
SELECT s.full_name, sub."Id" AS subscription_id, sub.status, p."Id" AS payment_id, p.status AS payment_status,
       p.amount, p.currency, p.received_amount, p.received_currency
FROM students s
LEFT JOIN subscriptions sub ON sub.student_id = s."Id"
LEFT JOIN payments p ON p.subscription_id = sub."Id"
WHERE s.full_name LIKE '[STAGING TEST DATA]%';

-- notification_outbox — proves an event was queued without claiming real delivery
SELECT event, channel, status, created_at_utc FROM notification_outbox ORDER BY created_at_utc DESC LIMIT 20;
```

None of these mutate anything. Never run this database's credentials
against `mvteaches_local` or vice versa.

## 7. What Local Staging does **not** exercise (and why that's correct, not a bug)

- **Starting a real meeting / publishing new slots.** The seeded teacher
  has no Zoom/Google connection on purpose — faking OAuth tokens would
  weaken the real "not ready for online sessions" production rule. Connect
  a real account from `/Teacher/Connections` (signed in as
  `staging-teacher@...`) to test this path; everything else (placement,
  purchase, manual payment, booking an already-seeded session) works
  without it.
- **Real WhatsApp/SMS delivery.** `notification_outbox` still records
  every real event (query it per §6); nothing is sent to a phone. See §5
  of the main task and Phase 5 notes for OTP specifically — no OTP
  delivery channel exists in this codebase at all yet (see the separate
  gap note, not fixed by this task).
- **Real online payment.** No provider is selected anywhere in this
  codebase (Development, Staging, or Production) — every purchase creates
  a Draft subscription; the existing manual-payment-method flow (§5's
  seeded CliQ method) is the one real way to fund it, identical in every
  environment.

## 8. Stopping Local Staging / going back to Development

Local Staging runs as its own process, started by
`scripts/run-local-staging.ps1` — closing that terminal (or Ctrl+C) stops
it. It never touches Development's process, port, or database, so
Development can be started or left running (in Visual Studio or a second
terminal) at the same time with no conflict — different ports, different
databases, environment-suffixed cookie names, and separate Data
Protection/upload folders (§2).

To remove the worktree entirely later (optional, only when this branch of
work is fully merged or abandoned):

```powershell
git worktree remove عون-staging-setup
git branch -d staging-setup   # only after it's merged, or -D to discard
```

This never touches `mvteaches_staging`'s data — dropping that database (if
ever wanted) is a separate, manual, explicit step in pgAdmin, same
warning as `mvteaches_local`'s own reset procedure in
`/docs/LOCAL-DEVELOPMENT.md` §9.
