# Testing the system from scratch

Owner decision 2026-09-04. This document **prepares** a clean-slate walkthrough
and deliberately does **not** perform one. Nothing here has been run against
Local Staging, and no data has been deleted.

The goal the owner set: keep only the main admin account, then walk a real
student and guardian journey from the very beginning.

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

## Option A — a second, empty database (recommended)

**Nothing is deleted. Local Staging stays exactly as it is.**

A separate database means the fresh-start walkthrough and the accumulated
evidence can both exist. It costs one `CREATE DATABASE` and one connection
string, and it is the only option here with no way to lose anything.

1. Create an empty database alongside the existing one:

   ```sql
   CREATE DATABASE mvteaches_freshstart;
   ```

2. Point a run at it by editing `ConnectionStrings:MvTeaches` in
   `src/MVTeaches.Web/appsettings.Staging.secrets.json` — the gitignored file
   Local Staging already reads (see `docs/LOCAL-STAGING.md`). Change only the
   database name.

3. Start the app. Migrations run on startup, so the schema builds itself,
   and `DataSeeder` creates the reference data every install needs: countries,
   levels, age groups, roles, and the seven courses.

4. Create the first admin through the existing one-time Bootstrap mechanism
   (`Bootstrap:*` in that same secrets file) — the same path the current
   admin account came from. **No admin password is written down here or
   anywhere in the repository.**

5. Turn the demo seeder OFF for this run so the database stays genuinely
   empty of people: leave `StagingSeed:Enabled` unset or `false`. Everything
   `StagingSeeder` creates is test data marked `[STAGING TEST DATA]`, and the
   point of this exercise is to have none of it.

To go back, change the connection string back. The original database was never
touched.

---

## Option B — empty the existing Local Staging database

**This destroys data and is not reversible without a backup.** Only worth it
if the owner specifically wants the same database rather than a second one.

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
worth exercising, in order. Every step below is a feature built this week:

1. **A guardian signs themselves up** at `/Account/Register` → "I am a parent
   or guardian". Phone number required.
2. **The guardian adds two children** on `/Guardian/MyChildren`. Each gets
   their own student number.
3. Confirm both children start with **no level, no package, no sessions** —
   and that the page says a level is needed before anything can be bought.
4. **An adult student signs themselves up** at the same page → "I am the
   student". Confirm they get no guardian, which is what lets them buy.
5. **The admin sets a level per course** on `/Admin/Students`. Give one child
   a level in English and a different level in a second course; confirm both
   stand at once.
6. **The admin grants a teacher a course and level** on `/Admin/Teachers`, and
   confirm the teacher cannot publish a session in a course they were not
   granted.
7. **The admin creates a weekly class** on `/Admin/Schedules` with a seat count
   other than 4, and confirms sessions generate at that capacity.
8. **The guardian buys a package** for one child. Confirm the child's own login
   (if given one) cannot buy for themselves.
9. **Pay 40 of 50, then the remaining 10.** Confirm the package activates once,
   with one ledger entry.
10. **Try to buy the same package again.** Confirm it is refused.
11. **Link a guardian to the wrong student, then unlink them** with a reason,
    and confirm the packages, payments and remaining hours all survive.

---

## What is deliberately not here

- **No reset script is committed.** The owner asked for a safe method, and a
  committed `reset.ps1` is a loaded gun in a repository that also contains the
  real staging connection. Option A needs no script at all.
- **No credentials.** Not the admin's, not the seeded accounts'. The Bootstrap
  and `StagingSeed` mechanisms already own that, and repeating a password in a
  document is how it ends up in a screenshot.
- **Nothing has been executed.** This is a plan awaiting the owner's word.
