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
| Zoom Account ID | `Zoom__AccountId` | No (until Zoom S2S app exists) | App runs fine without it — every Zoom call fails loudly with a clear "not configured" error instead of pretending to work |
| Zoom Client ID | `Zoom__ClientId` | No | — |
| Zoom Client Secret | `Zoom__ClientSecret` | No | — |
| WhatsApp Phone Number ID | `WhatsApp__PhoneNumberId` | No (until Meta verification completes) | Same "fails loudly" behavior |
| WhatsApp Access Token | `WhatsApp__AccessToken` | No | — |
| SMTP Host | `Smtp__Host` | Recommended | D-57's OTP backup channel — this one is a REAL, working implementation once configured |
| SMTP Port / Username / Password | `Smtp__Port`, `Smtp__Username`, `Smtp__Password` | Depends on your provider | — |

**Important:** leaving Zoom/WhatsApp unset is safe — the app starts and
runs normally. Those features simply report "not configured" instead of
doing something. This is intentional (see STATUS.md).

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

**Not yet implemented.** ASP.NET Core's built-in health-check middleware
(`AddHealthChecks()` + `MapHealthChecks("/health")`) has not been added to
Program.cs. This is a small, well-understood addition — flagged here as a
known gap rather than silently skipped.

## 11. Rollback procedure

1. Stop the app.
2. If the deployment included a migration: `dotnet ef database update <PreviousMigrationName>` to roll the schema back.
3. Redeploy the previous build artifact.
4. Restart the app.

**Never roll back past a migration that has already run financial
transactions against the new schema** without restoring from a backup
taken before that migration ran — rolling the schema back does not undo
data written under the new shape.
