# Implementation Status — as of 2026-08-26 (updated)

This is the detailed backing for the chat-delivered final engineering
report. Update this file, don't let it drift from what's actually true.

## Implemented and tested (real PostgreSQL 16, not in-memory)

| Module | What exists |
|---|---|
| Solution/architecture | ASP.NET Core 10 modular monolith: Domain/Application/Infrastructure/Web/Tests |
| Domain layer | ~30 entity classes across People, Catalog, Scheduling, Attendance, Ledger, Delivery, Payroll, Subscriptions, Payments, Certificates, Homework, Files, Settings, Audit, Notifications, Migration, Placement |
| Database schema | 2 EF Core migrations, applied to a real Postgres 16 instance; every documented FK, CHECK constraint, and partial unique index from the study is present |
| D-83 attendance/ledger | `JoinAttendanceService` — first-Join-wins, concurrent-Join-safe, guardian-on-behalf-of-child, insufficient-balance rejection, session-not-started rejection. 9 tests, all against a real DB including a genuine two-task race. |
| Scheduling concurrency | Teacher double-booking physically rejected by a PostgreSQL EXCLUDE constraint; enrollment duplication rejected; ledger append-only enforced by a database trigger even against raw SQL. 5 tests. |
| Payments (D-11/D-14/D-38) | `PaymentService` — manual channel record/confirm/reject, full-payment-only ledger posting, payment-block lifted in the same transaction, idempotent against double-confirm and provider-webhook-replay. 4 tests. |
| Settings (D-65) | `ISettingsProvider` reading the `settings` table live; loud failure on a missing/malformed key instead of a silent default. |
| Integration boundaries | `IZoomMeetingProvider`, `INotificationSender` interfaces. Zoom and WhatsApp: honest "not configured" implementations (no fabricated API behavior). Email: a real, working SMTP sender. |
| Notification dispatch | `NotificationDispatchJob` — idempotent outbox scan, bounded batch, attempt-counted failures — registered as a Hangfire recurring job. |
| Web host | Program.cs wires EF Core+Npgsql+NodaTime, ASP.NET Core Identity (long keys), Hangfire+PostgreSQL storage, all Application services, an admin-only-protected Hangfire dashboard. **Actually run** against the real database: schema installs, background server starts, HTTP requests are served, the recurring job fires, `/hangfire` correctly 401s when unauthenticated. |
| RBAC seed | Five roles (Admin, SystemAdmin, Teacher, Guardian, Student) seeded idempotently at startup, confirmed present in the database. |
| Reference data seed | Three age groups (Kids/Teens/Adults, D-04) and ten settings defaults (§19.5), confirmed present in the database with the documented values. |
| Health check | `GET /health` — `DatabaseHealthCheck` checks Postgres connectivity only (optional integrations are deliberately excluded — their absence is expected, not unhealthy). Verified live: actually ran the app and curled `/health`, got `200 Healthy` against the real database. |
| Recurring-schedule generator (§15.3) | `ScheduleGenerationService` — materializes `ClassSession` rows from every Active `RecurringSchedule` out to an admin-configured horizon (`ScheduleGenerationHorizonWeeks` setting, D-65-style — never hardcoded); fully idempotent re-runs; a teacher-overlap collision (the database's own EXCLUDE constraint) or a `TeacherTimeOff` window is never silently dropped — both are recorded as a `ScheduleGenerationException` row for an admin to see. Registered as a nightly Hangfire recurring job (`schedule-generation`); the documented "manual run" path is the Hangfire dashboard's own admin-only "Trigger now" button on that same job, not a second code path. 5 tests. |
| Payroll declare/verify/pay cycle (§18.1/§18.2, D-26) | `PayrollService` — the full declare → verify → open period → aggregate → review → approve → mark paid → close pipeline, orchestrating the pre-existing `SessionDelivery`/`PayrollPeriod`/`PayrollLine` domain state machines. `PayrollRateResolver` implements the most-specific-wins rate lookup (§9.2/D-27). Separation of duties (§18.3 rule 3) is enforced on both verify and reject. A real bug found and fixed while wiring this up: `SessionDelivery.Verify` always divided by 60 as if every rate were hourly, silently mispaying any `PerSession`-rated teacher — it now branches on `RateUnit`. 9 tests, including a full end-to-end cycle and a `PerSession` flat-rate case. |
| Certificate progress & issuance (§27.1/§27.2, D-30/D-51/CONF-03/Q-27) | `CertificateService` — `LevelProgress` is fully recomputed (never incremented) every time a delivery is verified, summing minutes only for sessions the student both attended (D-83) AND had delivery-verified (§18), grouped strictly by (student, level, course) — never by subscription (CONF-03). Eligibility is read live against `settings.CertificateRequiredHours` (D-65), never snapshotted onto the student. Per Q-27's resolution, issuance is always a separate, explicit admin action — crossing the threshold alone never issues a certificate. Wired directly into `PayrollService.VerifyAsync` so the recompute can't be forgotten by a future caller. 9 tests. |

**41/41 automated tests passing** (9 attendance/ledger, 5 scheduling
concurrency, 4 payments, 5 schedule generation, 9 payroll, 9 certificates),
all against a real local PostgreSQL 16 cluster (not SQLite, not EF Core
InMemory) — because several of the invariants under test (the EXCLUDE
constraint, the partial unique indexes, the append-only trigger) cannot be
honestly exercised any other way.

## Two genuine documentation contradictions found and fixed while implementing

1. §30.1's notification matrix still listed the "attendance not recorded
   after 24h" alert that D-83 already eliminated.
2. §11's `levels.required_minutes` column directly contradicted D-65's
   later, more specific decision that the certificate-hours threshold is
   a single global setting. Removed from the schema and the study, with
   the reasoning documented in place.

Both are committed with full explanations — see the git log.

## NOT implemented — genuine gaps, not oversights

| Area | Status |
|---|---|
| Razor UI (any page) | **Not started.** No Dashboard, Student Register, Student Profile, Payment History, or any other screen exists yet. This is almost certainly the single largest remaining item — §14 of the master prompt treats the Visual CRM Dashboard as part of MVP. |
| Migration import pipeline | Domain model exists (`MigrationBatch`, `MigrationRecord`); no Excel/CSV parsing, validation, preview, or transactional import service exists. |
| Homework file upload/signed-URL chain | `FileRecord`/`Homework`/`HomeworkSubmission` entities exist; no object-storage integration (Cloudflare R2), no signed-URL generation, no virus scanning. |
| Authorization policies on controllers/pages | Roles are seeded; no `[Authorize]` policies, no guardian-scoped query filters, no IDOR test suite exist on any endpoint yet, because no endpoints beyond the default scaffolded Razor Pages exist. |
| Zoom real integration | Boundary only — see the "honest stub" note above. Requires reading Zoom's current API docs against a live account, which this engagement had neither. |
| WhatsApp real integration | Same — requires Meta Business verification, which is externally pending (README). |
| MEPS integration | Correctly NOT started — `D-88`: `Waiting for MEPS`. The payment architecture's provider boundary is ready to receive it. |
| CI pipeline | Not set up. |
| Formal security review pass | Not performed as a dedicated pass — security was built in as each piece was written (parameterized queries throughout via EF Core, admin-only Hangfire dashboard, append-only ledger enforced at the DB level, guardian-scoped authorization in JoinAttendanceService), but no dedicated IDOR/CSRF/XSS/rate-limiting audit has been run because there is almost no HTTP surface yet to audit. |
| Deployment to a real VPS/Cloudflare | Not done — no VPS exists yet for this project. The deployment guide describes the standard, correct steps; none of them have been executed against MVTEACHES's actual infrastructure. |

## Honest conclusion

This is a real, substantially-verified foundation — not a prototype and
not a pile of scaffolding. The highest-risk piece the repository itself
calls out (D-83's attendance/ledger model) is fully implemented and has
the most thorough test coverage in the codebase. But a large majority of
the MVP's user-facing surface (every screen) and several backend
subsystems (payroll, migration, certificates, scheduling generation) have
not been started. **This is not production-ready, and it is not yet a
usable application for a real user to click through.**
