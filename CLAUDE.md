# MVTEACHES — Standing Workflow Rules

These rules are permanent for this project and apply to every future
session, regardless of what the current task is. They exist to prevent
Staging and Live Candidate from ever being mixed up — in code, in
commits, or in what gets promoted from one to the other.

## Branches and worktrees

| Folder / worktree | Branch | Role |
|---|---|---|
| `عون-staging-setup` | `staging-setup` | Where every new change is developed and tried first |
| `عون` | `Mwotayyem-patch-1` | **Live Candidate** — the stable, approved version |
| `عون` (as a ref) | `backup/pre-staging-2026-08-31` | Fixed safety snapshot |

- **Every new change starts on `staging-setup` only.** Never make a
  first edit directly on `Mwotayyem-patch-1`.
- **`Mwotayyem-patch-1` is Live Candidate.** It is never touched except
  after the owner's explicit, separate approval for that specific
  change — approval to work on Staging is not approval to touch this
  branch.
- **`backup/pre-staging-2026-08-31` is a fixed safety snapshot.** Never
  commit to it, never rebase it, never use it as a base for new work.
- **Never do a full `merge` of `staging-setup` into `Mwotayyem-patch-1`.**
  Staging's branch carries Staging-only infrastructure (its own database,
  secrets, seeders, scripts) that must never reach Live Candidate. Moving
  an approved change across is always a deliberate, selective operation
  (cherry-pick of specific commits) — see "Staging to Live Candidate
  Promotion" below — never a merge of the whole branch.

## Commit hygiene (applies on `staging-setup`, always)

- **Every change is split into separate commits by kind:**
  - One commit (or set of commits) for Product/UI/Business changes —
    anything that should eventually be usable on Live Candidate.
  - A separate commit (or set of commits) for Staging/config/secrets/
    scripts — anything that only exists because of the Local Staging
    environment.
- **Never mix Product changes with Staging-only changes in the same
  commit.** If a single piece of work touches both, split it into at
  least two commits before considering it done — this is what makes a
  later selective promotion to Live Candidate possible without manually
  disentangling a mixed diff.

## Never promote these to Live Candidate

The following must never appear in any commit or change moved to
`Mwotayyem-patch-1`. They are Staging-only by nature:

- Staging configuration of any kind
- Secrets / passwords
- `mvteaches_staging` (the database name, connection strings referencing
  it, or anything hard-coded to it)
- Test users / test data / seeded fixture content
- The Staging seeder
- Launch scripts specific to Local Staging (e.g. `scripts/run-local-staging.ps1`)
- Fake/test OTP configuration
- Sandbox/test payment configuration
- Staging-only API endpoints
- Data Protection key paths specific to Staging
- Storage paths specific to Staging
- `App_Data` (Staging's persistent runtime folder)
- Publish output (`bin/LocalStagingPublish` or equivalent)
- Hard-coded `IsStaging()` branching that exists only to route around
  Staging's own setup — business logic must stay identical across
  environments; only configuration/providers differ

## Review Required

**Any change touching `Program.cs`, a `.csproj`, `appsettings*`,
database/migrations, authentication/authorization, OTP, payment, or any
API integration is Review Required** — it must be surfaced explicitly,
explained, and approved before being treated as routine, even while
working on `staging-setup` itself. These are exactly the surfaces where
a Staging convenience can silently become a Production risk if it is
promoted without a second look.

## Conflicts during promotion

**If a conflict appears while moving anything to `Mwotayyem-patch-1`,
stop immediately.** Do not resolve it without the owner's explicit
approval of the specific resolution — describe the conflict and wait.

## Push / deploy

**No `push` and no deploy without an explicit, separate command from the
owner** — approval to commit locally is not approval to push, and
approval to push is not approval to deploy.

---

## Staging to Live Candidate Promotion

When the owner asks to move a change they approved from `staging-setup`
to `Mwotayyem-patch-1`:

1. **Never do a full merge.** Promotion is always selective.
2. **Diff the commits**: identify exactly which commits exist on
   `staging-setup` but not on `Mwotayyem-patch-1`.
3. **Move only the clean Product changes** — never a commit that mixes
   Product with Staging-only content (per the commit-hygiene rule
   above; if such a mixed commit exists, say so and stop rather than
   trying to split it after the fact without approval).
4. **If everything under consideration is Product/UI only and safe**,
   report back in exactly this compact form and then stop — do not
   execute the promotion yet:

   ```
   SAFE: yes/no
   Commits:
   Files:
   Risks:
   Next command:
   ```

5. **If anything is risky** — a Review Required surface, any
   Staging-only content, or a predicted/actual conflict — **stop and
   show the warning clearly** instead of the SAFE report. Never
   downplay a Review Required finding to make a promotion look SAFE.
6. **Never execute the promotion itself until the owner approves it**,
   even when the report says `SAFE: yes`.

---

## UI/UX work

- **For any UI or design request**, use the `ui-ux-pro-max` skill and
  the 21st.dev MCP when available.
- **Scope them to design and user experience only** — CSS, Razor views,
  layout, components. Never use them to drive Git operations, database
  changes, or Staging infrastructure.
- **Never change business logic, database, payment, auth, or OTP while
  doing UI work.** If a UI request seems to require one of those, stop
  and flag it rather than folding it into the UI change.
