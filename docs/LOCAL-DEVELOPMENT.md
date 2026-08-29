# Local development — Visual Studio, PostgreSQL 16, pgAdmin

This is the one-time setup and the everyday `F5` workflow for running the
real MVTeaches application on your own machine. It does **not** describe
production/staging deployment — see `docs/deployment/` for that.

The whole point of this setup: after the one-time secret configuration
below, you never run a CLI command again. Press `F5`, the app migrates and
seeds its own local dummy data, and you sign in.

## 1. Prerequisites

- **.NET 10 SDK** (matches every project's `<TargetFramework>net10.0</TargetFramework>`).
- **Visual Studio 2026**, with the **ASP.NET and web development** workload.
- **PostgreSQL 16** installed locally, listening on `localhost:5432`.
- **pgAdmin** (installed alongside PostgreSQL, or separately) to inspect the database.

## 2. Get the code

```powershell
git clone https://github.com/Mwotayyem/MVTEACHES_Technical.git
cd MVTEACHES_Technical
git checkout Mwotayyem-patch-1
git pull
```

Confirm you're on the expected commit:

```powershell
git log -1 --format="%H %s"
```

## 3. Create the `mvteaches_local` database in pgAdmin

This project does **not** auto-create the database itself — only the
*schema inside* an existing, empty database (via migrations, on `F5`, see
§6 below). Create the database once, manually, in pgAdmin:

1. Open pgAdmin, connect to your local PostgreSQL 16 server (`localhost`, port `5432`).
2. Right-click **Databases** → **Create** → **Database…**.
3. Name it exactly **`mvteaches_local`** (this exact name matters — see §6's safety explanation).
4. Owner: your own PostgreSQL login role (whatever you use to connect in pgAdmin).
5. Save. Leave it empty — every table is created by EF Core migrations on first `F5`.

## 4. Open the solution

Open **`src/MVTeaches.slnx`** in Visual Studio 2026 (this is the real
solution file — not a `.sln`; it's the newer XML solution format, and
Visual Studio 2026 opens it the same way). `MVTeaches.Web` is already
marked as the default startup project in the checked-in solution file
(`DefaultStartup="true"` on its `<Project>` entry), so a fresh clone
opens ready to run — you should not need to right-click and "Set as
Startup Project" yourself. If your own Visual Studio remembers a
different startup project from a previous session (its per-user `.suo`
state takes precedence over the solution's own default once it exists),
right-click **MVTeaches.Web** in Solution Explorer → **Set as Startup
Project** once.

## 5. Configure User Secrets (one time)

Right-click the **MVTeaches.Web** project → **Manage User Secrets**. This
opens `secrets.json` for this project (never committed, never part of the
repository). Paste in, replacing every placeholder with your own real values:

```json
{
  "ConnectionStrings": {
    "MvTeaches": "Host=localhost;Port=5432;Database=mvteaches_local;Username=<your-postgres-username>;Password=<your-postgres-password>"
  },
  "Bootstrap": {
    "AdminEmail": "admin@local.test",
    "AdminPassword": "<choose-a-real-password-at-least-10-characters>"
  },
  "LocalDevelopmentSeed": {
    "Enabled": true,
    "SeedPassword": "<choose-a-real-password-at-least-10-characters>"
  }
}
```

Notes on each key:

- `ConnectionStrings:MvTeaches` — your own local PostgreSQL login. This is
  the **only** place this connection string may live; it is deliberately
  never in any committed `appsettings*.json` file.
- `Bootstrap:AdminEmail` / `Bootstrap:AdminPassword` — creates the very
  first Admin account, the day one login problem every fresh install has
  (this mechanism already existed before this local-dev setup — see
  `MVTeaches.Infrastructure/Identity/BootstrapAdminOptions.cs`). It only
  ever acts while the Admin role has zero members, so it is safe to leave
  configured indefinitely in your own local secrets.
- `LocalDevelopmentSeed:Enabled` — the master switch for everything in
  §6 below (auto-migrate + local dummy accounts/content). `false` by
  default in the committed `appsettings.Development.json`; you are
  turning it on here, in your own machine's secrets only.
- `LocalDevelopmentSeed:RequiredDatabaseName` — **not shown above because
  you don't need to set it**: `appsettings.Development.json` already
  defaults it to `mvteaches_local`, matching §3. Only set it yourself if
  you genuinely name your local database something else.
- `LocalDevelopmentSeed:SeedPassword` — the single shared password used
  for every seeded local-only account (Teacher/Guardian/Student — see §8).
  Pick anything you'll remember; it never leaves your machine.

## 6. Press F5

With `LocalDevelopmentSeed:Enabled` = `true` and the database from §3
created (empty), pressing `F5` runs, **in this exact order, every time the
app starts, in Development only**:

1. **Connectivity check** — a real database health probe against
   `ConnectionStrings:MvTeaches`. If PostgreSQL isn't reachable (not
   running, wrong port, wrong credentials), the app **still starts**, but
   a clear `Critical`-level message is written to the console log naming
   the problem, and neither migration nor seeding runs. See §11 for the
   safety story around this.
2. **Database-name guard** — the actually-connected database's real name
   is compared against `LocalDevelopmentSeed:RequiredDatabaseName`
   (`mvteaches_local`). A mismatch refuses migration **and** seeding
   outright, with a clear log message — this is what makes it impossible
   for a copy-pasted connection string to accidentally point this at a
   shared, staging, or production database.
3. **Migrations** — every pending EF Core migration is applied
   (`MvTeachesDbContext.Database.MigrateAsync()`), against the exact
   database you created in §3. This is the **only** place in the whole
   codebase migrations run automatically — everywhere else (staging,
   production) they are an explicit, separate deployment step
   (`dotnet ef database update`), never an automatic side effect of
   starting the app.
4. **Idempotent seeding** — the always-on reference data (roles, the six
   CEFR levels, the three seed countries, the one course, default
   settings — this part already ran in every environment, unrelated to
   this local-dev feature), then the local-only dummy accounts/content
   described in §8, only in Development, only with the flag on.
5. The web application starts normally. Hangfire's background job
   processing (notification dispatch, the 5-minute session reminder, the
   nightly schedule generator, session finalization) does not begin
   until **after** this whole sequence has already completed.

Repeated `F5` runs are safe: every seed check is "if it doesn't already
exist" — a second, third, or hundredth `F5` never creates a duplicate row.
Nothing here ever deletes or resets existing data.

The console output on a successful first run ends with a line from the
`MVTeaches.LocalDevelopmentSeed` logger confirming it's done; find your
local HTTPS URL from either that console window's own startup banner or
`src/MVTeaches.Web/Properties/launchSettings.json`'s `https` profile —
**`https://localhost:7216`** (with `http://localhost:5093` as the
plain-HTTP fallback) unless you've changed it.

## 7. Inspecting the database in pgAdmin

Once `F5` has run at least once, refresh `mvteaches_local` in pgAdmin
(right-click it → **Refresh**). You should see:

- `__EFMigrationsHistory` — one row per applied migration; this is how EF
  Core itself tracks what's already been run.
- Every application table (`students`, `teachers`, `class_sessions`,
  `pricing_plans`, `placement_test_versions`, `notification_outbox`,
  `operating_expenses`, and the rest) — all created by the migrations that
  just ran, not by any separate schema script.

## 8. Seeded local accounts

All Development-only, all created idempotently, all clearly local dummy
data — **none of this exists, or ever appears, unless
`LocalDevelopmentSeed:Enabled` is `true`.** Passwords are never printed or
committed anywhere; use the ones you chose in §5's `secrets.json`.

| Email | Role(s) | Password | Notes |
|---|---|---|---|
| *(the value you set for `Bootstrap:AdminEmail`)* | Admin **and** SystemAdmin | *(`Bootstrap:AdminPassword`)* | The pre-existing bootstrap mechanism creates it as Admin; the local-dev seeder additionally grants SystemAdmin to that same account so you can reach every admin-only screen, including `/hangfire`. |
| `local-teacher@mvteaches.local` | Teacher | *(`LocalDevelopmentSeed:SeedPassword`)* | Granted the A1 level via the real, audited `ITeacherLevelAuthorizationService.GrantAsync` call — not a raw database insert. Deliberately has **no** Zoom/Google connection (see §10 for exactly what that blocks). |
| `local-guardian@mvteaches.local` | Guardian | *(same seed password)* | Linked to two separate children, "Local Dummy Child One" and "Local Dummy Child Two" — neither has a login of their own, and neither has taken the placement test yet, so you can exercise both independently and verify they stay isolated (rule 3). |
| `local-student@mvteaches.local` | Student | *(same seed password)* | A **direct** student login (not a guardian's child) with **no placement result yet** — this is the account the walkthrough below uses to demonstrate the whole placement → purchase → booking flow from a cold start. |

Also seeded (visible once you sign in, not logins themselves):

- Published **A1 Group** and **A1 Private** pricing plans.
- A **`[LOCAL DUMMY DATA]`**-titled, published, active placement test (two
  trivial questions; every possible score maps to A1, so the walkthrough's
  outcome is deterministic regardless of which answer you pick — this is
  clearly-marked technical test content, never real academic material).
- One sample `OperatingExpense` row so the financial dashboard has
  something to show immediately.
- Two future `ClassSession` rows for the seeded teacher at A1 (one Group,
  one Private) — seeded directly, bypassing the teacher-publish flow,
  specifically so you can test booking without needing a real Zoom/Google
  connection first. See §10 for what this does and doesn't let you test.

## 9. Resetting your local database (manual, destructive — never automatic)

Nothing in this project ever drops or truncates your data automatically.
If you want a genuinely clean slate:

> ⚠️ **This permanently deletes every row in your local database.** Only
> do this on your own `mvteaches_local` database, never anywhere else.

1. In pgAdmin, right-click **`mvteaches_local`** → **Delete/Drop**.
2. Re-create it exactly as in §3.
3. Press `F5` again — migrations and seeding both run fresh, from empty.

## 10. Full functional walkthrough

With `F5` running and the seeded accounts from §8:

1. **Sign in as Admin** (`Bootstrap:AdminEmail` / `Bootstrap:AdminPassword`).
2. Go to **Placement Tests** (`/Admin/PlacementTests`) — inspect the
   seeded `[LOCAL DUMMY DATA]` version; it's already Published and Active.
3. Sign out, **sign in as the direct student** (`local-student@mvteaches.local`).
4. Go to **Purchase Package** (`/PurchasePackage`) first — you'll see the
   CTA to take the placement test, not a package list, because no result
   exists yet (rule 1, enforced server-side, not just this page hiding a list).
5. Go to **Placement Test** (`/PlacementTest`), take the free test, submit
   — you receive **A1** (guaranteed, per §8's note on the dummy test's scoring).
6. Return to **Purchase Package** — now you see the A1 Group and A1
   Private plans (and only those; not any other level).
7. Purchase one — it's created as a **Draft** subscription. The page says
   plainly that online payment isn't available yet (§11) and to contact
   the centre.
8. **Sign in as Admin** again, go to **Payments** (`/Admin/Payments`),
   record and confirm a manual cash/bank-transfer payment for that
   subscription — this is the real, existing, permission-protected
   "تسجيل باقة ودفع يدوي" exception path, the one production-real way to
   activate a purchase without a payment gateway.
9. Sign back in as the student — the purchased package's minutes are now
   active; **Purchase Package** is behind you, so go to **My Sessions**
   (`/Student/MySessions`) and book one of the two seeded future A1
   sessions.
10. To exercise attendance/debit/notifications: as Admin, you can inspect
    `notification_outbox` rows in pgAdmin (§12) to see the booking
    confirmation queued. A real **Join** press requires the session to
    have actually started (`StartsAtUtc <= now`) — wait for the seeded
    session's time to arrive, or seed a session closer to "now" yourself
    for faster testing. Once joined, check `/Admin/Payroll` (a payroll
    line appears once you verify delivery) and `/Admin/FinancialReport`
    (scheduled hours, the sample operating expense, net profit).
11. **Sign in as the Guardian** (`local-guardian@mvteaches.local`) and
    confirm the two children are completely separate: each has their own
    independent placement-test eligibility on `/PlacementTest`
    (picking one child never shows or affects the other's attempt,
    level, or balance).

## 11. What still needs external credentials

Nothing below is faked, simulated, or worked around — each is exactly as
real (and exactly as blocked) locally as it is in production, per the
owner's own explicit instruction not to build a fake payment provider or
fake OAuth tokens.

- **Online payment.** No payment provider is selected in this codebase at
  all. Every purchase — local or production — creates a Draft
  subscription; step 8 above (the existing manual cash/bank-transfer
  admin path) is the only way to activate one locally today.
- **WhatsApp delivery.** No Meta Business credentials/templates exist in
  this environment, so `NotConfiguredWhatsAppSender` is what's wired up —
  every notification event still writes a real, durable row to
  `notification_outbox` (query it directly in pgAdmin, or add a
  read-only admin page later if you want one), but nothing is actually
  sent to a phone. To eventually enable real sending, populate the
  `WhatsApp:PhoneNumberId` / `WhatsApp:AccessToken` /
  `WhatsApp:BusinessAccountId` secrets once Meta Business verification
  completes and a Meta test number is configured — the application runs
  correctly without any of this today.
- **Live Zoom/Google verification.** The seeded teacher has no connected
  video account (deliberately — faking OAuth tokens would weaken the real
  "not ready for online sessions" production rule). This blocks: starting
  a real meeting for a session (`Start` on `/Teacher/MySessions`), and
  publishing new slots from `/Teacher/PublishSlots` (it will correctly
  refuse with "not ready for online sessions"). It does **not** block
  anything in the walkthrough above — placement, purchase, manual
  payment, and booking an already-seeded session all work with no video
  connection at all. To test the video path locally, register a real
  Zoom OAuth app or Google Cloud project (see the `_comment_Zoom` /
  `_comment_GoogleMeet` notes in `appsettings.json` for exactly what
  each needs) and connect it from `/Teacher/Connections` while signed in
  as the seeded teacher.
