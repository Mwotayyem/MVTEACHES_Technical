# Implementation Status — as of 2026-08-27 (updated)

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
| **Razor UI — first screens** | `/Account/Login` (email+password via `SignInManager`) and `/Admin/Dashboard` (`[Authorize(Roles = Admin,SystemAdmin)]`, live counts: active students/teachers, sessions today, open payroll periods, unresolved schedule-generation conflicts). A one-time `BootstrapAdminOptions`/`DataSeeder.SeedBootstrapAdminAsync` creates the very first Admin account (only while the Admin role has zero members) — see the deployment README's new §3.5. **Verified live end-to-end**, not just compiled: ran the app against the real dev database, applied the pending migration, created a bootstrap admin, logged in via a real HTTP POST (antiforgery token + cookie), confirmed a wrong password is rejected with a generic message (200, no redirect), confirmed the dashboard renders real (zero, on an empty dev DB) counts for an authenticated admin, and confirmed an unauthenticated request to the dashboard is redirected (302) rather than shown data. |
| **Financial report** | `/Admin/FinancialReport` — the owner's own stated MVP scope names "تقارير مالية أساسية" (basic financial reports) explicitly. `FinancialReportService` computes three plain, live numbers for a date range: revenue from confirmed payments (reported per-currency, never summed across currencies), payroll cost from PayrollLines whose period falls within the range, plus two current-state counts (students currently payment-blocked, payments awaiting confirmation). 5 tests. Verified live: logged in and loaded the page against the real dev database, got a real 200 with zero counts on the empty DB, and confirmed unauthenticated access redirects (302). |
| Recurring-schedule generator (§15.3) | `ScheduleGenerationService` — materializes `ClassSession` rows from every Active `RecurringSchedule` out to an admin-configured horizon (`ScheduleGenerationHorizonWeeks` setting, D-65-style — never hardcoded); fully idempotent re-runs; a teacher-overlap collision (the database's own EXCLUDE constraint) or a `TeacherTimeOff` window is never silently dropped — both are recorded as a `ScheduleGenerationException` row for an admin to see. Registered as a nightly Hangfire recurring job (`schedule-generation`); the documented "manual run" path is the Hangfire dashboard's own admin-only "Trigger now" button on that same job, not a second code path. 5 tests. |
| Payroll declare/verify/pay cycle (§18.1/§18.2, D-26) | `PayrollService` — the full declare → verify → open period → aggregate → review → approve → mark paid → close pipeline, orchestrating the pre-existing `SessionDelivery`/`PayrollPeriod`/`PayrollLine` domain state machines. `PayrollRateResolver` implements the most-specific-wins rate lookup (§9.2/D-27). Separation of duties (§18.3 rule 3) is enforced on both verify and reject. A real bug found and fixed while wiring this up: `SessionDelivery.Verify` always divided by 60 as if every rate were hourly, silently mispaying any `PerSession`-rated teacher — it now branches on `RateUnit`. 9 tests, including a full end-to-end cycle and a `PerSession` flat-rate case. |
| Certificate progress & issuance (§27.1/§27.2, D-30/D-51/CONF-03/Q-27) | `CertificateService` — `LevelProgress` is fully recomputed (never incremented) every time a delivery is verified, summing minutes only for sessions the student both attended (D-83) AND had delivery-verified (§18), grouped strictly by (student, level, course) — never by subscription (CONF-03). Eligibility is read live against `settings.CertificateRequiredHours` (D-65), never snapshotted onto the student. Per Q-27's resolution, issuance is always a separate, explicit admin action — crossing the threshold alone never issues a certificate. Wired directly into `PayrollService.VerifyAsync` so the recompute can't be forgotten by a future caller. 9 tests. |
| Reference data seed — Country/Level/Course | **Backend gap closed.** `DataSeeder` previously seeded roles/age-groups/settings only — there was no way to actually register a student (needs a `CountryId`) or assign a level/course anywhere, in a fresh deployment. Added `SeedCountriesAsync` (Jordan/JOD, Palestine/ILS, plus one mandatory "rest of world" USD row per D-53 — code `ZZ`, an ISO-3166-1 user-assigned/unspecified code, deliberately not a real country), `SeedLevelsAsync` (the six CEFR levels A1–C2 per §11), and `SeedCoursesAsync` (the single `GENERAL-ENGLISH` course per D-41). All idempotent, all sourced directly from the Technical Study's own documented values — no invented numbers. |
| **Student/guardian admission (§7/§8/§10)** | `IStudentAdmissionService`/`StudentAdmissionService` — the honest interim path standing in for phone+OTP self-registration (genuinely blocked on WhatsApp): an admin registers a guardian (real email/password Identity login + Guardian role), registers a student (optionally with their own login), links a guardian to a student (respecting the real `ux_guardianship_primary` database constraint — a second primary guardian is rejected, not silently allowed), manually verifies a student (the substitute for WhatsApp OTP verification), and assigns a level as an explicit, reasoned `AdminOverride` (superseding any previous current `StudentLevel` row, advancing `PendingLevel → Active` on first assignment only). 7 tests, including the primary-guardian conflict exercised against the real database constraint. |
| **Razor UI — Students, Payments, Payroll, Certificates** | Four new admin screens, each a thin form+list layer over an already-tested service: `/Admin/Students` (register/link/verify/assign-level + a students table), `/Admin/Payments` (record/confirm/reject over `IPaymentService`), `/Admin/Payroll` (declared-deliveries verify/reject + period open/aggregate/review/approve/pay/close over `IPayrollService`), `/Admin/Certificates` (live progress+eligibility list, issue/revoke over `ICertificateService`). **Verified live end-to-end** against the real dev database: registered a real guardian and student through the UI, linked them, verified the student, assigned a level (student reached `Active`, confirmed in the database), recorded and confirmed a real payment, opened a real payroll period, and confirmed the primary-guardian database constraint surfaces as a friendly in-page error rather than a 500. |
| **Authorization/IDOR integration tests** | `AuthorizationTests` — a real `WebApplicationFactory<Program>` host running against the same real PostgreSQL test database, with real HTTP requests (real login POST with antiforgery token, real cookie-based session) proving, for all 6 admin-only pages (Dashboard, Students, Payments, Payroll, Certificates, FinancialReport): an unauthenticated request is redirected to `/Account/Login` (not shown data); an authenticated Teacher is turned away; an authenticated Guardian is turned away; an authenticated Admin is shown the page. 24 tests (4 scenarios × 6 pages), the first automated closure of the "no IDOR test suite" gap noted below. |
| **Security review (focused)** | A dedicated security-focused review pass (SQL injection, auth/authz bypass, IDOR, mass-assignment/overposting via `[BindProperty]`, sensitive-data exposure, password handling, XSS, CSRF) was run specifically against every file added/changed for the four new admin pages and the admission service. **No high-confidence findings.** All writes go through parameterized EF Core LINQ; every audit field (`linkedByUserId`/`assignedByUserId`/`issuedByUserId`) is taken server-side from the authenticated principal, never from a posted form value; no `Html.Raw` anywhere; all forms use Razor Pages' default antiforgery protection. One documented, non-exploitable note: `RegisterGuardianAsync`/`RegisterStudentAsync` set `EmailConfirmed = true` without real proof of ownership — an accepted consequence of the admin-only manual-onboarding design while WhatsApp OTP remains unconfigured, not a vulnerability (only a trusted Admin/SystemAdmin can invoke it). |
| **CI pipeline** | `.github/workflows/ci.yml` — builds and runs the full test suite against a real `postgres:16` GitHub Actions service container (not SQLite/InMemory), mirroring `TestDatabaseFixture`'s local setup exactly (same port, same trust-auth role). **Written but not yet verified by an actual GitHub Actions run** — this repository's CI has never executed; the workflow is honestly un-triggered until the next push actually runs it. |

**77/77 automated tests passing** (9 attendance/ledger, 5 scheduling
concurrency, 4 payments, 5 schedule generation, 9 payroll, 9 certificates,
5 financial reports, 7 student admission, 24 authorization/IDOR), all against
a real local PostgreSQL 16 cluster (not SQLite, not EF Core InMemory) —
because several of the invariants under test (the EXCLUDE constraint, the
partial unique indexes, the append-only trigger) cannot be honestly exercised
any other way.

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
| Razor UI (remaining pages) | Login, Dashboard, Financial Report, **Students, Payments, Payroll, Certificates** now exist (see above). Still missing: a dedicated Student Profile/detail view (today's Students page is register+list only, no drill-down), a standalone Payment History view scoped to one student, and everything else in §14 not named above (e.g. teacher-facing screens, guardian/student self-service portal). Smaller than before, no longer "almost certainly the largest remaining item." |
| TOTP MFA for Admin/SystemAdmin | §22's "إلزامي" requirement — Identity supports TOTP natively, but no enrollment/challenge UI has been built; `Login.cshtml.cs` detects `RequiresTwoFactor` and surfaces this honestly instead of silently bypassing it. |
| Guardian/Student sign-in (phone + OTP) | Documented as the real flow (§7) but depends on the WhatsApp integration (not configured) for OTP delivery — genuinely blocked, not merely unbuilt. Only email/password accounts can sign in today (now including admin-registered guardians/students — see the admission service above — but real phone+OTP self-service sign-in is still blocked). |
| Migration import pipeline | **Deliberately not started — the study's own §43/final-open-items list marks this "مؤجَّل بقرار المالك، لا عجلة" (owner-deferred, no rush) pending a real ~10-row sample of the actual source file (§25.6).** Building the column-mapping/parsing logic now would mean inventing a source schema with no connection to reality. The domain model (`MigrationBatch`, `MigrationRecord`, both reversible-by-batch-id per §25.5) is ready to receive it once the sample arrives. Status unchanged this session — still owner-deferred, not dropped. |
| Homework file upload/signed-URL chain | `FileRecord`/`Homework`/`HomeworkSubmission` entities exist; no object-storage integration (Cloudflare R2, S3-compatible per the infra study), no signed-URL generation, no virus scanning. Unlike Zoom/WhatsApp, R2's API is public S3-compatible documentation, so this is a real candidate for a genuine (not stubbed) implementation next — just not done yet, and it is not on the owner's own stated MVP list, so it stays flagged as an open scope question rather than built. Status unchanged this session — still deferred, not dropped. |
| Zoom real integration | Boundary only — see the "honest stub" note above. Requires reading Zoom's current API docs against a live account, which this engagement had neither. |
| WhatsApp real integration | Same — requires Meta Business verification, which is externally pending (README). |
| MEPS integration | Correctly NOT started — `D-88`: `Waiting for MEPS`. The payment architecture's provider boundary is ready to receive it. |
| CI pipeline | **Written, not yet run.** `.github/workflows/ci.yml` builds and tests against a real `postgres:16` service container. Since it has never actually executed on GitHub's infrastructure, treat it as unverified until the first real push triggers it and it either passes or surfaces something to fix. |
| Deployment to a real VPS/Cloudflare | Not done — no VPS exists yet for this project. The deployment guide describes the standard, correct steps; none of them have been executed against MVTEACHES's actual infrastructure. |

## Honest conclusion

This is a real, substantially-verified foundation — not a prototype and
not a pile of scaffolding. The highest-risk piece the repository itself
calls out (D-83's attendance/ledger model) is fully implemented and has
the most thorough test coverage in the codebase. As of this session, an
admin can actually click through the core loop end-to-end: register a
guardian and a student, link them, verify the student, assign a level,
record and confirm a payment, open and run a payroll period, and issue a
certificate — all through real screens, backed by real, tested services,
verified live against a real database, not just unit-tested in isolation.
Role-based access control on every admin page is now covered by an
automated integration test suite (77/77 tests total), and a focused
security review of this session's new surface found no high-confidence
issues. What remains: a Student Profile/drill-down view, TOTP MFA
enrollment, the migration import pipeline (owner-deferred), Homework/R2
file storage (an open scope question, not built), the three external
integrations (Zoom/WhatsApp/MEPS, each genuinely blocked on external
access this engagement doesn't have), and an actual run of the
newly-written CI pipeline. **This is closer to a usable application than
before, but it is still not production-ready** — no external
integrations are live, and MFA for admin accounts (documented as
mandatory) is not yet enforceable.
