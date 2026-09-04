# Testing the system from scratch

Owner decision 2026-09-04, restated the same day: *"a clean database as if we
had just handed the project to the platform owner for the first time — only the
main admin account. No students, no guardians, no teachers, no subscriptions, no
payments, no sessions, no compensations, no old test data."* And, in the same
breath: **do not delete the current Local Staging database.**

Both halves are satisfied by making a *second* database rather than emptying the
first. `scripts/new-clean-trial-db.ps1` does exactly that, and nothing in it can
do anything else — see below.

---

## Read this first: why nothing was emptied

Local Staging currently holds the evidence of two real bugs — the four
separately-paid identical subscriptions that led to the duplicate-purchase
guard, and three students carrying two guardians each from before the
one-guardian rule. Those rows are the reason several of the rules built this
week exist, and they are the only place that history is written down.

Emptying the database destroys that. It is also irreversible in a way none of
this week's migrations are. So this is a decision to take deliberately, once,
with a backup in hand — not a step to fold into a coding session.

---

## Option A — a separate clean-trial database (recommended, and scripted)

**Nothing is deleted. Local Staging stays exactly as it is.**

```powershell
.\scripts\new-clean-trial-db.ps1 -Run
```

That creates `mvteaches_cleantrial`, applies every migration to it, writes a
gitignored `appsettings.CleanTrial.secrets.json` pointed at it, and starts the
app on <https://localhost:7218> — a different port from Local Staging's 7217, so
both can run side by side and the trial can be abandoned or repeated at any
moment without disturbing anything.

### Why the script cannot damage the existing database

Not "is careful not to" — *cannot*:

- **It contains no `DROP`, no `TRUNCATE`, and no `DELETE`.** A script with no
  destructive statement in it cannot destroy anything, which is a stronger
  guarantee than one that could but promises not to.
- **It refuses if the target name is the Local Staging database**, so naming it
  by accident is a stop, not a disaster.
- **It refuses if a database of that name already exists**, rather than reusing
  or emptying it. That database might be somebody's trial run in progress, and
  the script has no way to know. A second name costs nothing.
- **It verifies before it creates.** If `psql` is not on PATH it stops rather
  than creating a database it could not first prove was new, and prints the
  two SQL statements to run by hand instead.
- **It neutralises the environment before running migrations.** `Program.cs`
  adds the Staging secrets file *after* the environment-variable provider, so
  that file outranks `ConnectionStrings__MvTeaches`. Under
  `ASPNETCORE_ENVIRONMENT=Staging` the migrations would therefore land on Local
  Staging's database. The script sets both variables explicitly for the
  `dotnet ef` call rather than assuming the shell is clean.

### Why the trial database stays empty of people

Three independent reasons, any one of which is sufficient:

1. The config file it writes sets `StagingSeed:Enabled` to `false` explicitly —
   not merely absent. Everything `StagingSeeder` creates is demo data marked
   `[STAGING TEST DATA]`, and the point of the exercise is to have none of it.
2. `StagingSeeder` independently refuses to migrate *or* seed unless the
   connected database's name equals its configured `RequiredDatabaseName`,
   which the trial database's name deliberately does not.
3. `LocalDevelopmentSeeder`'s own guard is hardcoded to Development and this
   runs as Staging.

What the application *does* create on first start-up is exactly two things:
`DataSeeder`'s reference data — roles, countries, levels, age groups, settings,
and the twenty-one courses — and the bootstrap admin, through the same one-time
`Bootstrap:*` mechanism the current admin account came from. **No password is
written down here or anywhere else in the repository**, and the script never
prints one.

The script finishes by reporting the row counts it can see, so "empty" is
something you read rather than something you take on trust.

### Going back, or going again

Change nothing: Local Staging is still on 7217 against its own database. To run
a second trial from scratch, `-DatabaseName mvteaches_trial2`. Each trial is a
new database and none of the earlier ones is touched.

---

## Option B — empty the existing Local Staging database

**This destroys data and is not reversible without a backup.** Only worth it
if the owner specifically wants the same database rather than a second one.
Nothing here is scripted or committed, deliberately.

Required first, without exception:

```bash
pg_dump --format=custom --file=mvteaches_staging_before_reset.dump mvteaches_staging
```

Keep that file somewhere outside the repository. It is the only way back.

Then the shape of the reset — deliberately written as a description rather
than a ready-to-run script, because a script that empties a database is a
script that will eventually be run by accident:

- Delete in dependency order, or use `TRUNCATE ... RESTART IDENTITY CASCADE`
  on the people-and-money tables: attendance records, entitlement ledger
  entries, session enrollments, payments, subscriptions, class sessions,
  recurring schedules, compensation requests, placement attempts, student
  levels, guardianships, students, guardians, teacher grants, teachers.
- Leave the reference tables alone: countries, levels, age groups, courses,
  pricing plans, roles.
- Leave the admin's own `AspNetUsers` row, its role membership, and its
  permission claims. Delete every other user.
- Do **not** touch `__EFMigrationsHistory`. Emptying it would make the
  application try to re-run every migration against a schema that already has
  them.

A concrete point of care: `entitlement_ledger` is append-only by design and
several invariants assume nothing is ever removed from it. Emptying it is only
safe if the subscriptions it refers to go too — which is why the order above
matters and why partial cleanups are worse than either extreme.

---

## The walkthrough to run afterwards

Once there is a database with an admin and nothing else, this is the journey
the owner asked to exercise, in order. Every step is a feature built this week.

**The admin sets the centre up**

1. Sign in as the bootstrap admin. Confirm the catalogue on any screen with a
   course picker already lists the **twenty-one courses** — English, Arabic and
   Spanish, conversation and general, kids' and adults', IELTS and TOEFL
   (preparation and foundation), Business English, SAT, IG, and Quran — and
   that every one of them offers levels A1–C2.
2. **Add a teacher** on `/Admin/Teachers`, then grant them a **course and a
   level** (the chip reads e.g. "IELTS Preparation B2"). Grant a second
   teacher the *same level in a different course* and confirm both stand — a
   grant in one course authorises nothing in another.
3. **Publish a package** on `/Admin/Subscriptions` for a specific (course,
   level) pair.
4. **Create a weekly class** on `/Admin/Schedules` with a seat count other than
   4, and confirm the generated sessions carry that capacity rather than
   silently reverting to the default.

**The families arrive on their own**

5. **A guardian signs themselves up** at `/Account/Register` → "I am a parent
   or guardian". Phone number required.
6. **The guardian adds two children** on `/Guardian/MyChildren`. Each gets
   their own student number, its own level, its own packages, its own sessions
   and its own balance — nothing is shared between siblings.
7. Confirm both children start with **no level, no package and no sessions**,
   and that the page says a level is needed before anything can be bought.
8. **An adult student signs themselves up** at the same page → "I am the
   student". Confirm they get no guardian, which is what lets them buy for
   themselves.

**Placement, purchase, payment**

9. **The admin sets a level per course** on `/Admin/Students`. Give one child a
   level in English and a *different* level in a second course, and confirm
   both stand at once and both are shown.
10. **The guardian buys a package** for that child. Confirm only packages for a
    (course, level) the child actually holds are offered — a level in English
    must not put a Spanish package on the shelf — and that the child's own
    login cannot buy for themselves while a guardian is registered.
11. **Pay 40 of 50, then the remaining 10.** Confirm the package activates
    once, with one ledger entry and no double credit.
12. **Try to buy the same package again.** Confirm it is refused while hours
    remain.

**Attending, and being made whole**

13. **The student books a session** from their own screen. Confirm only
    sessions in a course *and* level they hold are listed, that a full session
    says so instead of vanishing, and that the seat count moves.
14. **Join** the session at its start, and confirm attendance is recorded and
    the **balance drops by the session's minutes** — read as a live SUM, never
    a stored number.
15. **Mark a session not-delivered**, request **compensation** as the student,
    and approve it as the admin — confirming the suggestion is a suggestion and
    the admin still decides.

**And the correction path**

16. **Link a guardian to the wrong student, then unlink them** with a reason,
    and confirm the student, the guardian, the packages, the payments and the
    remaining hours all survive. Only the link goes.

---

## What is deliberately not here

- **No reset script.** `scripts/new-clean-trial-db.ps1` creates; it never
  destroys. Option B stays a description, because a committed `reset.ps1` is a
  loaded gun in a repository that also contains the real staging connection.
- **No credentials.** Not the admin's, not the seeded accounts'. The Bootstrap
  and `StagingSeed` mechanisms already own that, the script copies values it
  never displays, and repeating a password in a document is how it ends up in a
  screenshot.
- **Nothing has been run against Local Staging.** Its database has not been
  emptied, migrated, or modified for any of this.
