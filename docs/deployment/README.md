# MVTEACHES — Deployment Guide

This covers exactly what exists today (see `/docs/deployment/STATUS.md` for
what does not). It is written for someone who has never administered
PostgreSQL, Hangfire, or a Linux VPS before, per the owner's own situation.

---

## 1. Environments

| | Purpose | Where it lives |
|---|---|---|
| **Local development** | Your own machine | `dotnet user-secrets` (never a committed file) |
| **Staging** | Pre-production testing | Environment variables on the staging VPS |
| **Production** | The live site | Environment variables on the production VPS, set by whoever administers it — never in source control |

**Never put a real connection string, API key, or password in `appsettings.json`
or `appsettings.Development.json`.** Both are committed to Git. Use
`dotnet user-secrets` locally, and environment variables everywhere else.

---

## 2. Environment variables / secrets the app needs

All of these bind to the same names shown in `appsettings.json`'s
placeholder sections. ASP.NET Core reads environment variables with `__`
(double underscore) in place of `:`.

| Setting | Environment variable | Required? | Notes |
|---|---|---|---|
| Database connection | `ConnectionStrings__MvTeaches` | **Yes — app refuses to start without it** | `Host=...;Port=5432;Database=mvteaches;Username=...;Password=...` |
| **Data Protection keys path** | `DataProtectionKeysPath` | **Yes in production** | ⭐ See §2.1 below. Encrypts teachers' Zoom/Google OAuth tokens at rest. Must point at a **persistent volume outside the database and outside the deployed binaries** — otherwise every redeploy makes every stored token undecryptable and every teacher must reconnect |
| Zoom Client ID | `Zoom__ClientId` | No (until a Zoom OAuth app exists) | ⭐ A **user-authorized** Zoom OAuth app (Zoom "General App" with OAuth), NOT Server-to-Server. There is deliberately **no** `Zoom__AccountId` any more — each teacher authorizes their own Zoom account |
| Zoom Client Secret | `Zoom__ClientSecret` | No | — |
| Zoom redirect URI | `Zoom__RedirectUri` | Required if Zoom is used | Must exactly match a redirect URI registered on the Zoom app, e.g. `https://yourdomain/oauth/zoom/callback` |
| Zoom webhook secret token | `Zoom__WebhookSecretToken` | Only if Zoom webhooks are enabled | The app's *Secret Token* from Feature → Event Subscriptions. Without it `/webhooks/zoom` returns 404 and trusts nothing |
| Google Meet Client ID | `GoogleMeet__ClientId` | No (until a Google Cloud OAuth client exists) | ⭐ A Google Cloud project with the **Google Meet REST API** enabled and an OAuth consent screen for external users. Each teacher authorizes their own — a **free** Google account is sufficient |
| Google Meet Client Secret | `GoogleMeet__ClientSecret` | No | — |
| Google Meet redirect URI | `GoogleMeet__RedirectUri` | Required if Google Meet is used | e.g. `https://yourdomain/oauth/google/callback` |
| WhatsApp Phone Number ID | `WhatsApp__PhoneNumberId` | No (until Meta verification completes) | Same "fails loudly" behavior |
| WhatsApp Access Token | `WhatsApp__AccessToken` | No | — |
| SMTP Host | `Smtp__Host` | Recommended | D-57's OTP backup channel — this one is a REAL, working implementation once configured |
| SMTP Port / Username / Password | `Smtp__Port`, `Smtp__Username`, `Smtp__Password` | Depends on your provider | — |
| Bootstrap admin email | `Bootstrap__AdminEmail` | **Yes, for the very first run only** | Creates exactly one Admin account, and only while the Admin role has zero members — see below |
| Bootstrap admin password | `Bootstrap__AdminPassword` | Same | Must satisfy the app's own password policy (10+ characters) |

**Important:** leaving Zoom/Google Meet/WhatsApp unset is safe — the app
starts and runs normally. Those features simply report "not configured"
instead of doing something. This is intentional (see STATUS.md). Note the
practical consequence for video: with **neither** provider configured, no
teacher can connect an account, so every teacher shows as **"Not ready for
online sessions"** and no schedule can be created. Configure at least one
before going live.

---

### 2.1 Data Protection keys — the one setting that silently corrupts data if wrong

Teachers' Zoom/Google OAuth access and refresh tokens are stored encrypted
with ASP.NET Core Data Protection. The key ring is persisted to
`DataProtectionKeysPath`. If that path is **not** on a persistent volume:

- the app still starts, and connecting still appears to work;
- but after the next restart or redeploy the keys are gone, every stored
  token becomes permanently undecryptable, and **every teacher has to
  reconnect their account** before any meeting can be created again.

Nothing warns you at the moment it happens. Set it explicitly:

```bash
sudo mkdir -p /var/lib/mvteaches/dataprotection-keys
sudo chown mvteaches:mvteaches /var/lib/mvteaches/dataprotection-keys
sudo chmod 700 /var/lib/mvteaches/dataprotection-keys
# then, in the service environment:
DataProtectionKeysPath=/var/lib/mvteaches/dataprotection-keys
```

Back this directory up together with the database, and restore the two
together — a database restored without its matching key ring has
unreadable tokens. If you run in a container, mount it as a named volume,
never a path inside the image.

---

## 3. PostgreSQL 16 setup

### On the production VPS (Ubuntu example)

```bash
sudo apt update
sudo apt install -y postgresql-16
sudo -u postgres psql -c "CREATE ROLE mvteaches WITH LOGIN PASSWORD 'CHOOSE-A-REAL-PASSWORD';"
sudo -u postgres psql -c "CREATE DATABASE mvteaches OWNER mvteaches;"
```

Then set `ConnectionStrings__MvTeaches` to:
```
Host=localhost;Port=5432;Database=mvteaches;Username=mvteaches;Password=CHOOSE-A-REAL-PASSWORD
```

**Do not use `trust` authentication in production.** The local development
cluster this project was built against uses `trust` auth deliberately —
that is a throwaway, disposable setup for testing on a single developer's
machine, never a production pattern. Production must use `scram-sha-256`
(PostgreSQL 16's default) with a real password.

### Applying the schema

Migrations are a **deliberate, separate step** — the app itself never
auto-applies them on startup (see Program.cs's comment on this). Run this
yourself, once per deployment that changes the schema:

```bash
dotnet tool install --global dotnet-ef   # once, if not already installed
cd src
dotnet ef database update \
  --project MVTeaches.Infrastructure \
  --startup-project MVTeaches.Infrastructure \
  --connection "Host=localhost;Port=5432;Database=mvteaches;Username=mvteaches;Password=..."
```

### What the app seeds automatically (safe, idempotent)

On every startup the app seeds — but never overwrites if already
present — the five roles (Admin, SystemAdmin, Teacher, Guardian, Student),
the three age groups (Kids/Teens/Adults per D-04), and the `settings`
table's documented default values (§19.5). This is ordinary application
data, not a schema change, so it runs automatically and safely.

---

## 3.5. Signing in for the first time

Every account after the first is created by an existing Admin (D-28) — but a
fresh database has none. Set `Bootstrap__AdminEmail` and
`Bootstrap__AdminPassword` (or the `dotnet user-secrets` equivalent locally)
before the app's first startup; it creates exactly one Admin account, and
only while the `Admin` role has zero members — every later startup is a
no-op even if these are still set. **Remove both settings once you've logged
in once** — there is no reason to leave a password sitting in configuration
after it has done its one job. Sign in at `/Account/Login`.

Guardian/Student sign-in is documented as phone + OTP, which depends on the
WhatsApp integration (not yet configured — see STATUS.md) and has not been
built. Only email/password accounts (Admin/SystemAdmin/Teacher) can sign in
today.

## 4. Hangfire

Hangfire uses the **same PostgreSQL database** — no separate service, no
Redis (§30.3 of the technical study explicitly rejects that at this
scale). It creates its own `hangfire` schema automatically on first run.

The dashboard is at `/hangfire` and is **admin-role-protected**
(`AdminOnlyDashboardAuthorizationFilter`) — an unauthenticated request
gets a 401, not the dashboard. To reach it, log in as a user in the
`Admin` role.

---

## 5. Reverse proxy / HTTPS / Cloudflare

The app listens on plain HTTP internally (Kestrel). Put it behind a
reverse proxy that terminates TLS. A minimal Nginx example:

```nginx
server {
    listen 443 ssl;
    server_name your-domain.com;

    ssl_certificate     /etc/letsencrypt/live/your-domain.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/your-domain.com/privkey.pem;

    location / {
        proxy_pass         http://127.0.0.1:5000;
        proxy_set_header   Host $host;
        proxy_set_header   X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;
    }
}
```

If using Cloudflare in front of Nginx: set the Cloudflare SSL mode to
**Full (strict)** once you have a real certificate on the origin (Let's
Encrypt via Certbot is the standard free option). Do not use "Flexible"
mode — it leaves the connection between Cloudflare and your VPS
unencrypted.

**This has not been configured or tested against a live domain/VPS in
this engagement** — no production server exists yet to configure. The
steps above are the standard, well-documented pattern; they have not been
run end-to-end against MVTEACHES's actual infrastructure.

---

## 6. Backup procedure

```bash
pg_dump -h localhost -U mvteaches -Fc mvteaches > "mvteaches-$(date +%Y%m%d-%H%M%S).dump"
```

Run this on a schedule (a daily cron job is sufficient at this scale) and
copy the dump off the VPS (e.g., to Cloudflare R2, per the domain/hosting
study's existing storage choice).

## 7. Restore procedure

```bash
pg_restore -h localhost -U mvteaches -d mvteaches --clean --if-exists mvteaches-<timestamp>.dump
```

**Test this restore procedure on a non-production database before you
need it for real.** An untested backup is not a backup.

## 8. Application restart

```bash
sudo systemctl restart mvteaches-web    # once a systemd unit is set up — not yet created in this engagement
```

A systemd unit file has not been written yet — there is no VPS to install
it on. The standard ASP.NET Core pattern (`dotnet MVTeaches.Web.dll` run
under a systemd service with `Restart=always`) applies; see Microsoft's
official "Host ASP.NET Core on Linux with Nginx" documentation when the
VPS exists.

## 9. Log inspection

Structured logs go to stdout/stderr by default (`Microsoft.Extensions.Logging`'s
console provider). Under systemd, `journalctl -u mvteaches-web -f` shows
them live. No external log aggregation (e.g., Seq, ELK) has been wired up —
not required at this scale per the technical study's own infrastructure
sizing.

## 10. Health checks

`GET /health` — a minimal liveness/readiness probe (`DatabaseHealthCheck`)
that checks Postgres connectivity only. Returns `200 Healthy` when the
database is reachable, `503 Unhealthy` otherwise. Deliberately does NOT
check Zoom/WhatsApp/MEPS — those are optional-until-configured integrations
(see their own "not configured" stubs), and their absence is expected, not
a failure. Point your reverse proxy / uptime monitor / orchestrator's
liveness probe at this endpoint once a VPS exists.

## 11. Rollback procedure

1. Stop the app.
2. If the deployment included a migration: `dotnet ef database update <PreviousMigrationName>` to roll the schema back.
3. Redeploy the previous build artifact.
4. Restart the app.

**Never roll back past a migration that has already run financial
transactions against the new schema** without restoring from a backup
taken before that migration ran — rolling the schema back does not undo
data written under the new shape.

---

## 12. Video meeting providers (Zoom / Google Meet) — owner clarification 2026-08-29

The centre buys **no** video-meeting licences. Each teacher connects their
**own** account and MVTeaches creates meetings inside it. **No paid
subscription is required of any teacher — a normal free Google account is
sufficient.** A teacher with neither a usable Zoom connection nor a Google
account is shown as **"Not ready for online sessions"** and cannot be
assigned any (enforced server-side, not just hidden in the UI).

Also note §2.1 above: without a persistent `DataProtectionKeysPath`, every
teacher's stored tokens become undecryptable on the next redeploy.

### 12.1 Zoom — a USER-authorized OAuth app (NOT Server-to-Server)

The earlier Server-to-Server design is dead. Do **not** create an S2S app
and do **not** look for a centre `AccountId`.

1. At `marketplace.zoom.us` → *Develop* → *Build App*, create a **General
   App** configured for **OAuth** (user-managed), not Server-to-Server.
2. Add the production redirect URI **exactly**:
   `https://<your-domain>/oauth/zoom/callback`
3. Add **only** these scopes — do not request account-admin or
   master-account scopes; requesting them will also make Marketplace review
   harder without giving MVTeaches anything it uses:
   - `user:read:user`
   - `meeting:write:meeting`
   - `meeting:read:meeting`
4. (Optional but recommended) *Feature* → *Event Subscriptions*: point the
   webhook at `https://<your-domain>/webhooks/zoom`, subscribe to
   `meeting.deleted` and `meeting.ended`, and copy the **Secret Token**
   into `Zoom__WebhookSecretToken`. Zoom will call the endpoint once to
   validate it; the app answers that challenge automatically. Without the
   secret configured, the endpoint returns **404** and trusts nothing.
5. Copy Client ID / Client Secret into `Zoom__ClientId` / `Zoom__ClientSecret`.
6. **Marketplace distribution:** an app in Development mode can only be
   authorized by users on the developer's own Zoom account. For independent
   external teachers to connect their own accounts, the app must be
   published/distributed accordingly — **confirm Zoom's current requirement
   for your case before launch**; this is an external gate MVTeaches cannot
   satisfy from code.

### 12.2 Google Meet — a teacher-authorized OAuth client

1. Create a Google Cloud project.
2. Enable the **Google Meet REST API** for it.
3. Configure the **OAuth consent screen** with User Type **External**.
4. Create an **OAuth 2.0 Client ID** of type *Web application*, with the
   authorized redirect URI **exactly**:
   `https://<your-domain>/oauth/google/callback`
5. Request **only** these scopes:
   - `openid`
   - `email`
   - `https://www.googleapis.com/auth/meetings.space.created`
     (creates and reads only the spaces this app itself created — not
     `meetings.space.readonly`, which would grant more than is needed)
6. Copy Client ID / Client Secret into `GoogleMeet__ClientId` /
   `GoogleMeet__ClientSecret`, and the redirect URI into
   `GoogleMeet__RedirectUri`.
7. **App verification:** Google restricts unverified external apps
   (consent warnings and user caps). For teachers outside your own
   organization to connect, the app will need to go through Google's
   verification/publication process — **confirm Google's current
   requirement before launch**; another external gate.

### 12.3 What the limits actually mean operationally

| Teacher's account | What MVTeaches allows |
|---|---|
| Zoom **Licensed** (paid) | Full-length sessions, whatever the session's configured duration |
| Zoom **Basic** (free) | Sessions of **40 minutes or less only**. A longer session is refused at creation with a message offering the legitimate options (connect Google Meet · upgrade their own Zoom · shorten the session). The limit is **never** worked around by chaining consecutive meetings |
| **Free Google account** | One-to-one (session capacity = 1): up to **24 hours**. Group (capacity > 1): up to **60 minutes**, and exactly 60 is allowed **with a warning** that Google may end it at the boundary |

Capability is judged from the session's **configured seat capacity**, not
its current booking count — a session that *may* admit a second student is
treated as a group session even while only one student has booked.

Google paid status is **never assumed**. Google exposes no reliable way for
a third-party app to prove a consumer account is paid, so the free limits
always apply. A teacher on Google Workspace who needs longer group sessions
should connect Zoom instead, or the session should be shortened.

---

## 13. Launch checklist — what is still required from the owner

Everything below is **external** to this repository. The code for all of it
is written and tested; none of it can be verified without these.

### 13.1 Zoom (external)

- [ ] A real Zoom OAuth app (General App, user-managed OAuth) → `Zoom__ClientId`, `Zoom__ClientSecret`
- [ ] Production redirect URI registered on that app → `Zoom__RedirectUri`
- [ ] Webhook endpoint configured + its Secret Token → `Zoom__WebhookSecretToken` (optional; skip and the endpoint stays 404)
- [ ] Whatever Marketplace activation / distribution / review Zoom currently requires for **independent external teachers** to authorize the app
- [ ] At least one real **paid/Licensed** Zoom teacher account
- [ ] At least one real **Basic (free)** Zoom account, to verify the 40-minute rejection against a real account rather than a test double

### 13.2 Google (external)

- [ ] A Google Cloud project
- [ ] Google Meet REST API enabled on it
- [ ] OAuth consent screen configured (External)
- [ ] OAuth Client ID + Secret → `GoogleMeet__ClientId`, `GoogleMeet__ClientSecret`
- [ ] Production redirect URI registered → `GoogleMeet__RedirectUri`
- [ ] Google app verification/publication, as required for external teacher accounts
- [ ] At least one real **free** Google account
- [ ] Event-subscription configuration, only if Google events are later adopted (nothing in MVTeaches depends on them today)

### 13.3 Shared production requirement

- [ ] `DataProtectionKeysPath` pointing at a **persistent volume outside the database** (see §2.1) — and included in the backup/restore procedure alongside the database dump

### 13.4 Live end-to-end verification (run once the above exist)

Until every line here has actually been run against real accounts, **live
Zoom/Google Meet integration must not be described as verified**:

- [ ] Paid Zoom teacher hosts a **full-length** session end to end
- [ ] Basic Zoom teacher is **refused** a session longer than 40 minutes, with the options message shown
- [ ] Free Google **one-to-one** meeting (session capacity 1) runs
- [ ] Free Google **group** meeting of no more than 60 minutes runs
- [ ] Two different teachers, on **independent** accounts, host **simultaneous** sessions
- [ ] A student's Join is authorized correctly (enrolled → in; not enrolled → out) and consumes exactly once
- [ ] Proof that **no student ever receives host privileges** — a student's Join lands on the participant URL only, never a Zoom `start_url`

### 13.5 Explicitly unchanged by this work

- MEPS / payment-provider integration was **not touched**. `D-88` stands: no
  replacement gateway has been selected, and alternatives are still being
  evaluated by the owner.
- WhatsApp remains blocked on Meta Business verification, exactly as before.
