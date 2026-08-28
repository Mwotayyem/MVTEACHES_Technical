# Implementation Status — as of 2026-08-28 (updated)

## Release-readiness audit (2026-08-28)

With the agreed MVP feature set complete (all four personas, all named
business areas), a full audit was run across authorization/IDOR, financial
integrity, concurrency, validation, migrations-from-clean-database,
background jobs, production configuration, secret handling, logging, error
handling, navigation/broken links, and misleading UI actions. Five real
defects were found and fixed, each with a new regression test, run against
real PostgreSQL:

1. **Dead links to a deleted page (misleading UI).** `/Admin/Schedules`'
   session-cancellation help text and its own post-cancel status message
   both still pointed admins to `/Admin/MakeUpCredits` — deleted in the
   2026-08-27 reschedule/compensation redesign (see that entry above). Both
   now point to the real page, `/Admin/RescheduleSessions`. Several stale
   doc comments referencing the deleted `IMakeUpCreditService` (in
   `ISessionCancellationService`, `SessionCancellationService`, and their
   tests) were also corrected to reference the real replacement,
   `IEnrollmentService.ApproveReplacementLessonAsync`, so a future reader
   is not sent looking for a type that no longer exists.
2. **SystemAdmin locked out of the Hangfire dashboard.**
   `AdminOnlyDashboardAuthorizationFilter` checked only the literal
   `"Admin"` role — a hardcoded string matching none of this codebase's own
   `RoleNames` constants, and critically excluding `SystemAdmin`, the role
   `RoleNames.cs` itself documents as "elevated over Admin" for exactly
   this kind of operational control. A SystemAdmin-only account could not
   reach `/hangfire` at all (job history, manual trigger of the nightly
   schedule-generation job), unlike every other admin surface in the app,
   which authorizes Admin and SystemAdmin together. Fixed to check both
   roles via the real constants. No test had ever exercised this filter —
   4 new tests added (unauthenticated, wrong role, Admin, SystemAdmin).
3. **Financial integrity: a real double-credit race on payment
   confirmation.** Unlike `ux_ent_consumption` (D-83's own backstop on the
   spend side), there was no database-level constraint preventing two
   *different*, legitimately-distinct `Payment` rows on the SAME
   subscription from both being confirmed concurrently and both posting a
   `Purchase` ledger entry — `PaymentService.SettleSubscriptionIfFullyPaidAsync`'s
   "already posted?" check is a plain SELECT with no ambient serializable
   transaction. Reproduced live against real PostgreSQL before fixing:
   two payments confirmed via `Task.WhenAll` on separate DbContexts
   produced 2 Purchase entries instead of 1, i.e. the subscription's
   minutes were double-credited. Fixed with a new partial unique index,
   `ux_ent_purchase` on `entitlement_ledger(subscription_id)` filtered to
   `reason = 'Purchase'` (migration `AddPurchaseLedgerUniqueIndex`), and a
   retry-safe catch in `PaymentService.ConfirmAsync`: the losing
   confirmation's own payment is still marked Confirmed (it is real money
   received) but does not re-attempt the now-redundant ledger insert —
   the same outcome the sequential-overpayment case already produced,
   reached safely under a real race. New regression test proves exactly 1
   Purchase entry and both payments Confirmed.
4. **Concurrency: an unhandled 500 on a certificate-issuance race.**
   `CertificateService.IssueAsync`'s "already issued?" pre-check is also a
   plain SELECT; the real backstop is
   `UNIQUE(student_id, level_id, course_id)` on `certificates`, but unlike
   every other race-guarded write in this codebase (Join, Enroll, payment
   confirm), the resulting `DbUpdateException` on a concurrent double-issue
   was never caught — it would have surfaced as an unhandled 500 instead
   of the friendly `AlreadyIssued` outcome the sequential case already
   returns. Fixed with the same catch-and-retry pattern used everywhere
   else. New regression test (two concurrent `IssueAsync` calls on
   separate DbContexts) proves exactly one certificate survives and the
   loser gets `AlreadyIssued`, not a crash.
5. Confirmed clean: migrations apply from a genuinely empty PostgreSQL 16
   database (`TestDatabaseFixture` drops and recreates the test database
   and runs `MigrateAsync()` at the start of every full test run — this
   was exercised, not assumed, by every one of the 3 consecutive full-suite
   runs below), every `_Layout.cshtml` navigation link resolves to a real
   page, every `[BindProperty]` complex input on every page calls
   `TryValidateModel` before proceeding, no secrets are committed anywhere
   in the repository (config files only ever contain empty
   placeholders/env-var instructions), and no logging statement anywhere
   emits a password, token, or other secret (only ids and non-sensitive
   status text).

**156/156 automated tests passing**, 3 consecutive clean runs against real
PostgreSQL 16, after this audit's fixes. (Superseded by the self-service
booking correction below, which adds 27 more — see the final **183/183**
count further down.)

This is the detailed backing for the chat-delivered final engineering
report. Update this file, don't let it drift from what's actually true.

## ⭐⭐ Owner correction (2026-08-28) — student self-service booking, supersedes part of D-83

After the release-readiness audit above, the owner issued an explicit
correction that **replaces two interpretations this codebase had built
around** — recorded formally as `D-89`/`D-90` in
`MVTEACHES_Owner_Answers_R3.md`'s new addendum section. This is not an
additive feature; it changes standing behavior, so the superseded
interpretations are removed here, not left alongside the new ones.

**What changed, and why:** "Admin assigns the student's normal lesson
dates" (the interpretation every prior session built against) does not
scale to hundreds or thousands of students. The corrected model: a student
is assigned a level → purchases a finite package → sees, in their own
portal, only future sessions matching their own level → books their own
specific dates themselves. **Admin no longer manually assigns normal
sessions for every student.** The admin-initiated flows this codebase
already had (`/Admin/Schedules`'s enroll form, `/Admin/RescheduleSessions`'s
two forms) are unchanged and still valid — they are not how a student's
*normal* sessions get chosen day to day anymore, but nothing about them was
wrong or removed.

**The part that actually reverses prior behavior — D-90, superseding half
of D-83:** D-83's original rule was "a student who never presses Join is
never debited, no exceptions." That was correct for the admin-assigns
model (a missed admin-assigned slot is generally the centre's scheduling
matter, not the student's choice). It stops being correct once the student
picks their own specific session from open availability: missing a
session **they themselves chose** now consumes the scheduled duration
exactly once, the same as a real Join, once the session ends and no Join
was ever pressed. `AttendanceRecord` — previously documented as "only ever
one status: Present... Absent is derived, never written" — now carries an
explicit `IsPresent` bool; a no-show is a real, written outcome, not a
derived absence. A centre-cancelled or administratively rescheduled
session is never affected by this — it's never in `Scheduled` status by
the time this would apply, so it can't reach this path at all.

**What did NOT change:** compensation is still never a spendable credit,
wallet, or automatic grant (D-19/D-20/D-55, unchanged) — a replacement is
still exactly one admin-approved future session, linked back to the
missed one, Joinable for free exactly once. The only addition on that side
is *who initiates the request* (the student, from their own portal, not
only the admin acting directly) and a real notification once approved.

The full implementation — domain model, migration, services, both Razor
pages, and every regression test — is documented in the next section, in
place of (not alongside) the earlier, now-superseded description of
`/Student/MySessions` as "calls `IJoinAttendanceService` and nothing
else" further down this file.

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
| **CI pipeline** | `.github/workflows/ci.yml` — builds and runs the full test suite against a real `postgres:16` GitHub Actions service container (not SQLite/InMemory), mirroring `TestDatabaseFixture`'s local setup exactly (same port, same trust-auth role). **Verified by real GitHub Actions runs.** Two early runs (commits `e8d8d1a`, `b1812a0`) failed — see the race-condition entry below, which is what they actually caught. Green as of the fix. |
| **A genuine race condition, caught by CI (not local runs) and fixed** | `JoinAttendanceServiceTests.Two_concurrent_join_requests_still_produce_exactly_one_consumption` failed nondeterministically on GitHub's runners (never locally) — full logs showed one of the two racing requests got `InsufficientBalance`/`IsPresent=false` instead of the required idempotent "present" result. Root cause: `JoinAttendanceService.JoinAsync` makes its "already recorded?" fast-path check, its balance check, and its insert as three independently-committed statements (no ambient transaction spans them). A losing request could read "not yet recorded" (true at that instant), then read the balance *after* the winning request's concurrent commit had already drained it — reporting a false `InsufficientBalance` instead of recognizing the winner's attendance now exists. Fixed by re-checking `AttendanceRecords` immediately before returning `InsufficientBalance`: if a concurrent request for the exact same (session, student) won the race in the meantime, this request now correctly returns `AlreadyRecorded` (idempotent, present) instead of a spurious balance error. The database's own unique constraints remain the sole correctness guarantee against a genuine double-write (unchanged) — this fix only corrects which *outcome* the loser reports. Confirmed by running the concurrency test 40 consecutive times against real PostgreSQL with no failures, then the full suite (116/116). |
| **Financial-integrity confirmation: the Join write is genuinely one atomic transaction** | Requested check, done from the actual code and the live schema (not assumed): `JoinAttendanceService.JoinAsync` adds the `AttendanceRecord` and the `EntitlementLedgerEntry` (Consumption) to the same `DbContext` and calls `SaveChangesAsync` exactly once — EF Core's default behavior wraps every pending change in ONE call to `SaveChangesAsync` in a single implicit database transaction (no `EnableRetryOnFailure`/custom execution strategy is configured anywhere in `Program.cs`, which is the only thing that would change this). Verified live against the real database (`psql \d`) that both invariants actually exist as real Postgres objects, not just EF configuration: `ux_attendance_session_student` (UNIQUE on session_id, student_id) and `ux_ent_consumption` (a partial UNIQUE index on session_id, student_id filtered to `reason = 'Consumption'`) — either one firing (Postgres error 23505) aborts the whole transaction, rolling back both writes together; there is no code path where one insert can commit while the other fails. There is no separate stored "balance" to update or desynchronize — balance is always `SUM(delta_minutes)` computed live (D-36), so the only two writes in question are the attendance row and the ledger row. **New regression test** `A_ledger_side_conflict_rolls_back_the_attendance_insert_too_no_orphan_debit` forces a unique-constraint failure specifically on the ledger side (a pre-existing Consumption row for the same session+student, with no attendance row yet) and confirms the attendance insert — which would have succeeded on its own — is rolled back with it: 0 attendance rows, exactly 1 ledger Consumption row (the pre-existing one, not a duplicate) after the call. Run 10 consecutive times against real PostgreSQL with no failures, then the full suite (117/117). |
| **Recurring schedules (§15.2, D-23)** | `IRecurringScheduleService`/`RecurringScheduleService` + `/Admin/Schedules` — **closed the single most fundamental gap in the whole repository**: until this pass, there was no way anywhere in the application to create a `RecurringSchedule`, which meant no `ClassSession` could ever exist outside a test's own direct database insert, which meant attendance, payroll declare/verify, and certificate progress could never be exercised against real, admin-created data in a real deployment. Create/pause/resume, with the manual "run generation now" path deliberately left as the Hangfire dashboard's own admin-only button on the existing `schedule-generation` job (a standing decision from an earlier session, not reopened here — no second code path was added). 3 tests. Verified live: created a real recurring schedule through the UI and confirmed every field landed correctly in the database. |
| **Teacher admission (§9.1, D-28)** | `ITeacherAdmissionService`/`TeacherAdmissionService` + `/Admin/Teachers` — there was also no way to create a Teacher account at all before this. Mirrors the guardian-registration pattern exactly (real Identity login + Teacher role + domain entity), plus deactivate/reactivate. Verified live: registered a real teacher through the UI. |
| **Teacher pay rates (§9.2, D-27)** | `ITeacherRateService`/`TeacherRateService`, folded into `/Admin/Teachers` — closes the last missing piece of the payroll chain: without a `TeacherRate` row, `IPayrollService.VerifyAsync` could never succeed against a real teacher (the resolver, already tested, always found an empty table). 2 tests. Verified live as part of the full payroll chain below. |
| **Teacher portal (§18.1 step [1])** | `/Teacher/MySessions` — the teacher-facing half of the payroll cycle had no UI at all before this pass (only the admin-side verify/reject on `/Admin/Payroll` existed). A teacher sees their own sessions (30 days back, 7 forward) and declares delivery on the ones that have actually ended. **A real IDOR was found and fixed during this session's own security review** (see below) before this shipped. |
| **Subscriptions & pricing plans (§23, §19.2, D-13/D-38)** | `ISubscriptionService`/`SubscriptionService`, `IEntitlementBalanceQuery`/`EntitlementBalanceQuery` + `/Admin/Subscriptions` — closes the other half of "purchase and payment": the domain model (`PricingPlan`/`Subscription`/`EntitlementLedgerEntry`) and `IPaymentService`'s own full-payment-activates-and-posts-Purchase logic already existed and were already tested, but there was no way to create a pricing plan or a subscription in the first place. Admin can create a plan, purchase a Draft subscription against it for a student (activated only once `/Admin/Payments` confirms a matching payment in full — D-38's no-partial-activation rule, unchanged), or grant one free with an immediate AdminGrant ledger entry (D-13). 5 tests. **Verified live, the complete chain**: created a pricing plan → purchased a Draft subscription for a real student → recorded and confirmed a real payment against it on `/Admin/Payments` → confirmed the subscription flipped to `Active` and a `Purchase` ledger entry for the correct minutes was posted, all in the real database. |
| **The full payroll chain, verified live end-to-end** | With schedules, teachers, and rates now buildable, this session proved the entire declared→verified→aggregated→approved→paid cycle for real, not just in tests: created a recurring schedule, inserted one real ended session, declared delivery as the teacher through `/Teacher/MySessions`, verified it as the admin through `/Admin/Payroll` (payable amount correctly computed from the real rate), opened a payroll period, aggregated, moved to review, approved, and marked paid — final state confirmed in the database (period `Paid`, delivery `Paid`, correct amount). |
| **A real bug found and fixed via this same live testing** | `/Admin/Payroll`'s "open period" handler didn't catch the real `UNIQUE(country_id, period_start, period_end)` constraint, so re-opening an existing period crashed with a 500 instead of the friendly message every other duplicate-constraint case in this app already shows. Found by hitting it live, fixed, and re-verified live. |
| **Session enrollment + the Guardian portal — D-83's real front door (§15.1/§15.4, §16)** | `IEnrollmentService`/`EnrollmentService` closes a gap the Technical Study itself leaves open: §15.4 states "session_enrollments مستقل عن آلية الإسناد" (session_enrollments is independent of the assignment mechanism) but never specifies that assignment mechanism's own schema anywhere (no CREATE TABLE for it exists in the study, matching `SessionEnrollment.cs`'s own long-standing doc comment). Rather than inventing a new persistent "group membership" table to fill that gap, this implements exactly what the schema already has: enroll a student directly into one or many upcoming sessions of a recurring schedule, with §15.1's real atomic conditional `UPDATE ... WHERE seats_taken < capacity` (a plain read-then-write is documented as failing under concurrency) and the age-group-at-enrollment snapshot (§12.2). New admin action on `/Admin/Schedules`. Then `/Guardian/MyChildren` — **the first UI anywhere for `IJoinAttendanceService`**, the single highest-risk, most heavily-tested piece of backend in the whole repository (D-83, 9 dedicated tests including a real concurrency race), which had never been exercised through any screen before this. 4 new `EnrollmentService` tests. **Verified live, for real, for the first time in this project's history**: enrolled a real student into a real upcoming session, logged in as their actual guardian, confirmed the session correctly showed as not-yet-joinable before it started, waited for it to start, pressed Join — attendance was recorded and a real `-60` minute `Consumption` ledger entry was posted against the `+600` minute `Purchase` entry from earlier in this session (net balance correctly at 540) — and confirmed a second Join press is a true no-op (still exactly one attendance row, one consumption entry). The open assignment-mechanism question is flagged below, not silently resolved. |
| **Third focused security review** | Reviewed `IEnrollmentService`/`EnrollmentService`, the `/Admin/Schedules` enroll form, and `/Guardian/MyChildren`. Specifically checked whether the guardian portal's Join handler (which takes a raw `studentId` from the POST body with no page-level re-check) is an IDOR like the earlier Teacher one — it is **not**: `IJoinAttendanceService.JoinAsync` independently re-verifies the acting user is actually an authorized guardian of that specific student before writing anything, using a server-derived acting-user id, so the safety net exists one layer down regardless of what the page does. Also confirmed the raw `ExecuteSqlInterpolatedAsync` calls in `EnrollmentService` are properly parameterized (no string concatenation of untrusted input) — no SQL injection. No findings. |
| **Student Profile drill-down (§14)** | `/Admin/StudentDetails/{id}` — the piece the register/list page (`/Admin/Students`) never had: one screen bringing together everything else built this session for a single student — guardians, level history, subscriptions with live balances, payment history, certificates. Purely a read aggregation over already-tested data (no new business logic). 4 tests (unauthenticated, wrong-role, real student, 404 for a nonexistent one — not a crash). Verified live against the real dev database. |
| **Second focused security review + a real fix** | Reviewed every file added in this pass (subscriptions, teacher admission, schedules, teacher rates, the teacher portal). Found one genuine **High-severity IDOR**: `/Teacher/MySessions`'s declare handler took a bare session id with no check that it belonged to the calling teacher, so any authenticated Teacher-role account could declare delivery on a different teacher's session (falsifying the "declared by" audit trail and locking the rightful teacher out with an `AlreadyDeclared` state). Also found a Medium finding: the "session must have already ended" rule was enforced client-side only. Both fixed in the same page handler (ownership check + server-side end-time check) and proven with a new automated regression test that creates two real teachers and confirms the cross-teacher declare attempt is rejected and leaves no `SessionDelivery` row behind. |
| **Session cancellation (D-20)** | The owner's own MVP scope names "makeup sessions" explicitly as a line item — until this pass, `ClassSession.Cancel`/`CancelAndReplace` (domain methods from an earlier pass) were never wired to anything; there was no way anywhere in the app to cancel a session at all. `ISessionCancellationService`/`SessionCancellationService`, wired into `/Admin/Schedules` (which now also lists upcoming sessions with real ids to act on), cancels a whole session outright or with a direct replacement — a replacement transfers every not-yet-consumed enrollment (`SessionEnrollment.MarkTransferred`) with **no ledger movement**, while a student who already pressed Join before the problem occurred is left completely untouched. |
| **Reschedule & per-student compensation (D-19/D-20/D-55, owner clarification 2026-08-27)** | An earlier version of this pass built a standalone spendable `MakeUpGranted` ledger-credit mechanism for compensation. **The owner reviewed it and rejected that design outright**, giving an explicit, definitive two-case clarification that replaces it entirely (see the git history for the reverted commit's own honest CONF-04 flag — this is exactly that flag being resolved, by the owner, not by this session inventing an answer): **Case 1 — the student never pressed Join.** Nothing was ever consumed, so their balance is already untouched; the admin is just moving that specific unused lesson-hour to a new time. `IEnrollmentService.RescheduleUnattendedEnrollmentAsync` marks the original enrollment `Transferred` and creates a fresh one on the replacement session (reusing `EnrollInSessionAsync`'s own atomic capacity check) — no ledger entry of any kind, because none was ever needed. Rejects if the original was actually already consumed. **Case 2 — the student DID press Join**, and the session then failed for a reason outside their control (§17.4/line 1018 — the one case the Technical Study reserves for the admin's own judgment). The original attendance and consumption are never touched. `IEnrollmentService.ApproveReplacementLessonAsync` approves exactly ONE specific replacement session, recorded via a new nullable `SessionEnrollment.CompensatesForSessionId` column (migration `AddSessionEnrollmentCompensation`) linking the replacement enrollment back to the original session and the approving admin. `IJoinAttendanceService.JoinAsync` checks this field and — only for that one specific enrollment — skips `FindConsumableSubscriptionAsync`/the debit entirely, recording just the `AttendanceRecord`. **This is deliberately not a spendable credit**: there is no pool, no balance, no expiry sweep, no admin-set deadline field, and no separate ledger reason — the replacement is tied to exactly one real, admin-chosen session and is usable exactly once, the same as any other enrollment (the `AttendanceRecord` unique constraint is what makes "exactly once" true, same as every other Join). New `/Admin/RescheduleSessions` page exposes both cases as two clearly separate forms. The previously-flagged CONF-04 ambiguity is now resolved by this clarification and no longer open. 8 new tests replace the 5 removed makeup-credit ones (net +3), including a real end-to-end proof: student Joins the original (real consumption) → admin approves a replacement → student Joins the replacement for free (zero ledger entries for that session, original consumption untouched) → a second Join on the same replacement is idempotently rejected, not double-recorded. Full suite run 3 consecutive times clean against real PostgreSQL. |
| **TOTP two-factor authentication (§22)** | §22's "MFA إلزامي لـ SystemAdmin وReadOnlyAdmin" — until this pass, `Login.cshtml.cs` could only detect `RequiresTwoFactor` and tell the user it wasn't built yet. Now fully functional using ASP.NET Core Identity's own built-in TOTP support (`AddDefaultTokenProviders`, already registered) — no invented crypto, no new dependency: `/Account/ManageMfa` (enrollment — a real shared key + manual-entry setup, since adding a QR-rendering library was avoidable scope; verify with a live 6-digit code, one-time recovery codes shown exactly once, regenerate, disable), `/Account/LoginWith2fa` (the TOTP challenge step, using Identity's own intermediate "passed password, pending 2FA" cookie via `GetTwoFactorAuthenticationUserAsync`/`TwoFactorAuthenticatorSignInAsync`), `/Account/LoginWithRecoveryCode` (lost-device fallback). This codebase's closed 5-role set (`RoleNames`) has no distinct "ReadOnlyAdmin" from the study's own §22 wording — Admin/SystemAdmin are the two roles this applies to, matching how this gap was already framed in this file before being built. **Enforcement is an honest nudge, not a hardened block:** `Login.cshtml.cs` redirects an Admin/SystemAdmin account straight to `/Account/ManageMfa` right after a successful password check if `TwoFactorEnabled` is still false, instead of their original destination — every login until they comply. It is **not** a per-request middleware/claims-transformation block on every later page, which would need a bigger change than this pass made; typing a different URL directly still works for an Admin who hasn't enrolled. Also not added: a re-authentication/current-password step before `OnPostDisableAsync` turns MFA off — this matches ASP.NET Core Identity's own official scaffolding default (the same baseline, not a novel weakness introduced here), noted rather than silently left unmentioned. 4 new tests, including a full real round trip: register → login → redirected to enrollment → real RFC 6238 code computed from the actual shared key → verified → logged out → logged back in → redirected to the 2FA challenge (not enrollment) → a fresh real code → full sign-in completes → a real admin page loads. No mocked verification anywhere in that chain. |
| **Teacher pay history (§18) — closes the last named UI gap** | `/Teacher/MyPayHistory` — until this pass, a teacher could declare delivery (`/Teacher/MySessions`) but had no way to see what they'd actually earned once admin verified, aggregated, reviewed, approved, and paid it. Purely a read-only report over the already-tested `PayrollLine`/`PayrollPeriod` data (no new Application-layer method, no new business logic) — the same "query the DbContext directly for a report" pattern `/Admin/Payroll`'s own `LoadAsync` already uses. Resolves the acting teacher from the authenticated account's own linked `Teacher` row server-side, exactly like `/Teacher/MySessions` and `/Student/MySessions` — no teacher id is ever accepted as a request parameter, so there is no "which teacher" input to attack. Shows a per-currency summary (amount actually **Paid** — periods in `Paid`/`Closed` status — versus **Pending**, verified work not yet paid) plus the itemized line list (session date, course/level, minutes, rate, amount, period, period status). 5 new tests: unauthenticated redirect, wrong role turned away ×2 (Admin, Guardian), shown to the right role, and a data-isolation regression test that seeds a distinctive payroll amount for a *different* teacher and proves it never appears on the calling teacher's own page while their own line does. |
| ⭐⭐ **Student self-service booking + no-show compensation (D-89/D-90, owner correction 2026-08-28) — supersedes the description above and part of D-83** | See the addendum section above this table for the full "why". Implementation: **(1) `IStudentBookingService`/`StudentBookingService`** — a NEW entry point distinct from `IEnrollmentService.EnrollInSessionAsync` (the admin/guardian path, unchanged), because the trust boundary differs: a student booking for themselves needs three checks together that nothing else in this codebase needed before — their own account (never a request parameter), the session's level matching their own current `StudentLevel` (server-resolved on both sides, never trusted from the client), and that booking it wouldn't push consumed-plus-committed-but-unconsumed minutes for that course/level past what their active package(s) can cover. That third check is a genuinely new derived quantity (`ledger balance − committed minutes of not-yet-resolved active bookings`) with no single-row constraint able to express it, so — the same class of problem §15.1's atomic seat claim solves for capacity — it's guarded by an explicit transaction holding a `SELECT ... FOR UPDATE` row lock on the student's own row, serializing that one student's concurrent booking attempts; two different sessions each individually affordable, booked at the same instant, can no longer both succeed. Reuses the existing atomic seat-claim SQL for capacity. **(2) `AttendanceRecord.IsPresent`** (migration `SelfServiceBookingAndNoShowCompensation`, backfilled `true` for every existing row, `MarkedByUserId` now nullable — `NULL` means the system itself, matching `EntitlementLedgerEntry`'s own existing convention) — the class-level doc comment's old "only ever one status: Present... Absent is derived, never written" is removed; a no-show is now a real, once-only written outcome, guaranteed by the same `ux_attendance_session_student` unique index that already guaranteed "first Join wins". **(3) `ISessionFinalizationService`/`SessionFinalizationService`**, a new Hangfire recurring job (`session-finalization`, every 5 minutes — a no-show should resolve promptly, not overnight) — finds `Scheduled` sessions past `EndsAtUtc`, and for every enrolled student with no attendance row yet, writes `AttendanceRecord(IsPresent:false, MarkedByUserId:null)` plus the same `Consumption` ledger entry a real Join would have written (skipped, correctly, for a replacement enrollment — its cost was already paid by the original session), then marks the session `Completed`. A cancelled session never enters this at all (`Status != Scheduled` excludes it from the query outright), which is the entire mechanism behind "a centre-cancelled session never consumes hours" — no separate check needed. `JoinAttendanceService` was updated so a late Join arriving after finalization reports a new, honest `JoinOutcome.AlreadyFinalizedAsNoShow` instead of falsely claiming `AlreadyRecorded`/present. **(4) `CompensationRequest`** (new table, migration `SelfServiceBookingAndNoShowCompensation`, partial unique index `ux_compensation_request_open` allowing at most one Pending-or-Approved request per missed session — a Rejected one doesn't block a fresh attempt) + **`ICompensationRequestService`/`CompensationRequestService`** — the student submits one request linked to their own confirmed no-show (verified via the same `AttendanceRecord.IsPresent == false` row the finalizer wrote, never trusted from the client); `ApproveAsync` does not duplicate any granting logic, it calls the pre-existing `IEnrollmentService.ApproveReplacementLessonAsync` verbatim, which now additionally enforces the replacement being the **same level** and a **future** session (two outcomes that didn't need checking before this method could only be reached by a trusted admin choosing directly) — then, only after that call succeeds, creates the `NotificationOutboxItem` (`NotificationEvent.ReplacementLessonApproved`, a newly-approved closed-enum member; `NotificationChannel.WhatsApp`; payload = student name, level, replacement date, replacement time) as a deliberately separate step, mirroring `CertificateService`'s own "the recompute is a separate round trip after Verify" precedent — a request that's merely submitted, or rejected, creates zero notifications. This is the first real usage of the outbox anywhere in the codebase (previously prepared, never wired to an actual trigger); `NotConfiguredWhatsAppSender` still means no message is claimed to actually deliver without real Meta credentials — only that the durable, retryable intent to send is now genuinely created and tested. **(5) Web**: `/Student/MySessions` rebuilt with three sections (enrolled sessions with Join/Request-replacement actions, available sessions filtered to the student's own level with a Book action, and the student's own request history) — the identity/level resolution happens the same server-side way on every handler; `/Admin/CompensationRequests` (new, admin-only) is the review queue (student, level, original session, reason) with Approve (choosing a candidate replacement session) / Reject actions. **27 new tests** across `StudentBookingServiceTests` (9, including a genuine concurrent-database-race proof that two simultaneous bookings by the same student cannot together exceed one package, and a second proving capacity concurrency through this new entry point specifically), `SessionFinalizationServiceTests` (6, including a genuine concurrent race between a live `JoinAttendanceService.JoinAsync` call and `SessionFinalizationService.FinalizeEndedSessionsAsync` on separate DbContexts via `Task.WhenAll`, proving exactly one attendance row and one consumption entry survive either way), `CompensationRequestServiceTests` (8, including the notification-only-after-approval guarantee checked at every stage: zero after submission, zero after rejection, exactly one and only one after a successful approval), plus one new `/Admin/CompensationRequests` role-gating case in `AuthorizationTests`. All concurrency tests re-run 5 consecutive times individually with no failures, in addition to the full-suite runs below. |

**183/183 automated tests passing** (10 attendance/ledger, 5 scheduling
concurrency, 4 payments, 5 schedule generation, 9 payroll, 9 certificates,
5 financial reports, 7 student admission, 5 subscriptions, 2 teacher rates,
3 recurring schedules, 4 enrollment, 8 session cancellation, 8 reschedule/
compensation, 4 MFA, 62 authorization/IDOR — 10 admin pages × 4 role scenarios,
4 teacher-portal-specific, the cross-teacher IDOR regression test,
4 Student Details tests, 4 guardian-portal-specific, 4 student-portal-specific,
4 pay-history-specific, the pay-history cross-teacher isolation regression test,
5 payment/certificate/Hangfire audit-fix regressions, 9 student booking,
6 session finalization, 8 compensation request, 1 CompensationRequests
role-gating case), all run 3 consecutive times clean against a real local
PostgreSQL 16 cluster (not SQLite, not EF Core
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
| Razor UI (remaining pages) | Login, Dashboard, Financial Report, Students, Payments, Payroll, Certificates, Teachers, Schedules, Subscriptions, Reschedule/Compensation, **Compensation Requests**, MFA, the Teacher portal (sessions + pay history), the Student detail drill-down, the Guardian portal, and the Student portal (now booking + no-show requests) all exist (see above). All four personas (Admin, Teacher, Guardian, Student) now have at least one real screen, and every named MVP-scope UI gap noted in earlier passes of this file is closed. |
| **Student portal (§7) — the last of the four personas** | `/Student/MySessions` — until this pass, `RoleNames.Student` was assigned at registration but referenced by zero pages anywhere; the Teens/Adults case with their own login (as opposed to a child covered by a guardian) had no screen at all. Originally mirrored `/Guardian/MyChildren` closely (called `IJoinAttendanceService` and nothing else). **Superseded by the 2026-08-28 self-service-booking correction above** — the page now also lets the student browse and book their own level's future sessions and request a replacement for a no-show; the description of what it does today lives in that entry, not this one. What's unchanged from this original pass: the acting student id is still always resolved server-side from the authenticated account's own linked `Student` row, never accepted as a request parameter. 4 original authorization tests (unauthenticated redirect, wrong-role turned away ×1, shown to the right role — including graceful degradation for a Student-role account with no linked `Student` row yet) still pass unmodified. |
| ✅ **§15.4's "assignment mechanism" question — resolved by the owner's 2026-08-28 correction, no longer open** | The prior open question ("should a student be auto-enrolled into a recurring schedule's future sessions, or does an admin re-run bulk-enroll periodically?") is now moot: **there is no more "assignment" happening on the student's normal sessions at all.** The self-service booking model means the student picks their own sessions one at a time, from open availability filtered to their level — neither of the two options this question posed. `IEnrollmentService.EnrollInUpcomingSessionsAsync` (the admin bulk-enroll action) still exists, unchanged, for admin/guardian-initiated cases, but it is no longer how a student's day-to-day sessions get chosen. No persistent "group membership" table was ever built, and per D-89 none is needed now. |
| Guardian/Student sign-in (phone + OTP) | Documented as the real flow (§7) but depends on the WhatsApp integration (not configured) for OTP delivery — genuinely blocked, not merely unbuilt. Only email/password accounts can sign in today (now including admin-registered guardians/students — see the admission service above — but real phone+OTP self-service sign-in is still blocked). |
| Migration import pipeline | **Deliberately not started — the study's own §43/final-open-items list marks this "مؤجَّل بقرار المالك، لا عجلة" (owner-deferred, no rush) pending a real ~10-row sample of the actual source file (§25.6).** Building the column-mapping/parsing logic now would mean inventing a source schema with no connection to reality. The domain model (`MigrationBatch`, `MigrationRecord`, both reversible-by-batch-id per §25.5) is ready to receive it once the sample arrives. Status unchanged this session — still owner-deferred, not dropped. |
| Homework file upload/signed-URL chain | `FileRecord`/`Homework`/`HomeworkSubmission` entities exist; no object-storage integration (Cloudflare R2, S3-compatible per the infra study), no signed-URL generation, no virus scanning. Unlike Zoom/WhatsApp, R2's API is public S3-compatible documentation, so this is a real candidate for a genuine (not stubbed) implementation next — just not done yet, and it is not on the owner's own stated MVP list, so it stays flagged as an open scope question rather than built. Status unchanged this session — still deferred, not dropped. |
| Zoom real integration | Boundary only — see the "honest stub" note above. Requires reading Zoom's current API docs against a live account, which this engagement had neither. |
| WhatsApp real integration | Same — requires Meta Business verification, which is externally pending (README). |
| MEPS integration | Correctly NOT started — `D-88`: `Waiting for MEPS`. The payment architecture's provider boundary is ready to receive it. |
| Deployment to a real VPS/Cloudflare | Not done — no VPS exists yet for this project. The deployment guide describes the standard, correct steps; none of them have been executed against MVTEACHES's actual infrastructure. |

## Incident: all 16 root documentation files found emptied on disk (2026-08-27)

Mid-session, all 16 non-code documents in the repo root (both Technical
Study versions, Owner Answers R3, every side-study, README, the summary
HTML) were found at 0 bytes on disk, with git still holding their full
committed content. Evidence gathered before touching anything: `git
status`, file sizes/timestamps (all 16 emptied within the same ~13ms
window, a strong single-bulk-operation signature — not 16 separate manual
edits), `git diff --numstat`, and confirmation every `HEAD` blob was
non-empty. This was not caused by anything in this session — none of the
session's own Write/Edit/Bash calls touched these files — and predates
this session's start (it was already showing as modified in the very
first `git status` this session was given). Restored all 16 exactly from
`HEAD` via `git checkout HEAD -- <file>` (never `git reset`, never a full
working-tree restore, nothing else touched), then verified: every file
non-empty, `git diff --stat` empty for all 16 (byte-identical to `HEAD`),
and a full-repo `git status` came back completely clean. Re-read the
restored Technical Study afterward to confirm §15.4's assignment-mechanism
gap (above) before building `IEnrollmentService` against it, rather than
building from memory of an earlier read.

## Honest conclusion

This is a real, substantially-verified foundation — not a prototype and
not a pile of scaffolding. The highest-risk piece the repository itself
calls out — D-83's attendance/ledger model — was, until this session,
fully implemented and heavily tested but had **never once been exercised
through an actual screen**. That changed this session: a guardian logged
in, saw their real child's real upcoming session, and pressed Join for
real — attendance was recorded and a real Consumption entry was posted
against the real Purchase entry from earlier in the same session, with a
verified idempotent no-op on a second press. Combined with everything else
built this session, an admin (and now a teacher, and now a guardian) can
click through the ENTIRE core loop end-to-end for real, starting from
nothing: create a recurring schedule, register a teacher and set their pay
rate, register a guardian and a student, link them, verify the student,
assign a level, enroll the student in a session, create a pricing plan and
purchase a subscription, record and confirm the payment that activates it,
have the teacher declare delivery through their own portal, verify it as
the admin, run the full payroll cycle through to Paid, issue a
certificate, and have the guardian actually press Join — all through real
screens, backed by real, tested services, verified live against a real
database at every step, not just unit-tested in isolation. A real,
High-severity IDOR was found by this session's own security review (a
teacher could declare delivery on another teacher's session) and fixed
with a regression test before shipping. Role-based access control on
every admin page, the teacher portal, and the guardian portal is now
covered by an automated integration test suite (116/116 tests total).
Three dedicated security-review passes were run this session, each
scoped to only the files added since the last pass: the first (Students/
Payments/Payroll/Certificates) found nothing; the second (Subscriptions/
Teachers/Schedules/rates/the teacher portal) found the IDOR above, fixed
and regression-tested; the third (enrollment/the guardian portal)
explicitly checked for the same class of bug and confirmed it does not
recur there, since `IJoinAttendanceService` independently re-verifies
guardian authorization regardless of what the page does. Mid-session, all
16 root documentation files were found emptied on disk by something
outside this session's own actions; evidence was captured first, then
they were restored exactly from `HEAD` with nothing else touched — see
above. What remains: a Student-facing self-service screen, TOTP MFA
enrollment, the migration import pipeline (owner-deferred), Homework/R2
file storage (an open scope question, not built), the three external
integrations (Zoom/WhatsApp/MEPS, each genuinely blocked on external
access this engagement doesn't have), an actual run of the newly-written
CI pipeline, and the open question (flagged above, not resolved) of how a
student gets auto-enrolled into a recurring schedule's future sessions.
**This is now a genuinely usable, end-to-end application for every one of
the three personas the MVP names** — but it is still not production-ready:
no external integrations are live, and MFA for admin accounts (documented
as mandatory) is not yet enforceable.
