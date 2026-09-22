# PR Checks — how it works

Companion doc for [`.github/workflows/pr-checks.yml`](.github/workflows/pr-checks.yml).

## What you asked for, and where each piece lives

| Requirement | Implemented by |
|---|---|
| Run tests, show pass/fail counts, go red on failure | `test` job + the step-summary step |
| Then build, go red if it doesn't compile | `build` job |
| Build must run **even when tests fail** | `needs: test` + `if: ${{ !cancelled() }}` |
| Runs on every PR | `on: pull_request` |
| Runs again on each new commit to an open PR | the default `synchronize` activity type — free |
| Both must pass before merge | branch ruleset (repo setting, not a file — see below) |

## The shape of the run

```
PR opened / new commit pushed
        │
        ▼
  ┌───────────┐   finishes, pass OR fail    ┌───────────┐
  │   Test    │ ──────────────────────────► │   Build   │
  └───────────┘                             └───────────┘
        │                                         │
        └──────────────► merge button ◄───────────┘
                    (unlocked only if both green)
```

---

## 1. The trigger

```yaml
on:
  pull_request:
    branches: [master]
```

`pull_request` has **activity types**. When you don't list them, you get the default three:

| Type | When it fires |
|---|---|
| `opened` | PR is created |
| `reopened` | a closed PR is reopened |
| `synchronize` | **a new commit is pushed to the PR's source branch** |

So your "I also want it to run when someone commits to a branch with an open PR" requirement needs no extra config — `synchronize` already does exactly that. Adding a separate `push:` trigger would only duplicate every run.

`branches: [master]` filters on the **target** branch (where the PR wants to merge *into*), not the source branch.

### Deliberately no `paths:` filter

The other workflows in this repo use `paths: ['dotnet/**']` to skip runs on unrelated edits. This one doesn't, and that is on purpose:

> A check that is **required** by branch protection but **skipped** by a `paths` filter never reports a status. The PR then sits at *"Expected — Waiting for status to be reported"* forever, and cannot be merged.

If you ever want path filtering on a required check, the workaround is a separate always-running job that reports a status on behalf of the skipped one — more machinery than it is worth here.

## 2. Two jobs, not two steps

Each **job** gets its own fresh VM and its own entry in the PR's checks list. Each **step** is just a command inside one job, and steps don't show up individually in branch protection.

Since you want *two independently-required checks*, they have to be two jobs. The cost: the `build` job re-runs `checkout` and `setup-dotnet` from scratch, because it's a different machine with nothing carried over from `test`.

## 3. The ordering trick

```yaml
  build:
    needs: test
    if: ${{ !cancelled() }}
```

These two lines do different halves of the job:

- **`needs: test`** — sequencing. Without it, both jobs start at the same time. With it, `build` waits for `test` to finish.
- **`if: ${{ !cancelled() }}`** — overrides the default. By default `needs` *also* means "if `test` failed, skip me." The `if` replaces that default condition, so `build` runs regardless of how `test` ended.

| `test` result | `build` runs? |
|---|---|
| success | yes |
| failure | **yes** — this is the behaviour you asked for |
| workflow cancelled | no |

### Why `!cancelled()` and not `always()`

`always()` is the one everyone reaches for first, and it has a sharp edge: it runs the job **even when you hit the cancel button** on the workflow. Runs become hard to stop. `!cancelled()` means "run unless the whole thing was cancelled", which is what people actually mean when they type `always()`.

(The one place `always()` *is* right here: the step-summary step inside `test`, where cancellation isn't the concern and you genuinely want the stats written no matter how the test step ended.)

## 4. Showing the test stats

`dotnet test` already prints a line like this:

```
Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 9 ms - Calculator.Tests.dll (net9.0)
```

(One line, one framework: `Calculator.Tests` single-targets `net9.0`, which is why `setup-dotnet` installs just `9.0.x` and neither command needs a `--framework` argument.)

That summary line is buried in the log, so we tee it to a file and republish it to the run summary:

```yaml
      - name: Run tests
        shell: bash
        run: dotnet test --logger "trx;LogFileName=results.trx" | tee test-output.txt
```

### The `shell: bash` line is load-bearing

This is the subtlest thing in the file. On Linux runners:

- **Default shell** (no `shell:` key) → `bash -e {0}`
- **Explicit `shell: bash`** → `bash --noprofile --norc -eo pipefail {0}`

Only the second one has **`pipefail`**. Without it, the exit code of `dotnet test | tee ...` is `tee`'s exit code — always `0` — and **a failing test suite would report as a green check**. Any time you pipe a command whose failure matters, set `shell: bash`.

### `$GITHUB_STEP_SUMMARY`

A file path GitHub hands to every step. Markdown appended to it renders on the run's summary page. It is built in — no third-party action, no extra token permission.

```yaml
      - name: Publish test stats to the run summary
        if: always()
        run: |
          {
            echo '### Test results'
            echo ''
            echo '```'
            grep -E '^(Passed|Failed)!' test-output.txt \
              || echo 'No test summary line found - the test project likely failed to compile.'
            echo '```'
          } >> "$GITHUB_STEP_SUMMARY"
```

The `|| echo` fallback matters: if the test project fails to *compile*, `dotnet test` never prints a counters line, `grep` exits non-zero, and under `-e` that would fail the step for the wrong reason.

**On reading a red `test` check:** it means either "an assertion failed" *or* "the test project didn't compile" — `dotnet test` builds before it runs. The summary line tells you which.

## 5. Two supporting lines

```yaml
permissions:
  contents: read
```

Least privilege. Without it, the job token gets the repository's default permissions (often write). These jobs only read code, so say so. Anything that needs more — posting a PR comment, pushing a tag — then opts in explicitly.

```yaml
concurrency:
  group: ${{ github.workflow }}-${{ github.ref }}
  cancel-in-progress: true
```

Push three commits in a minute and you would otherwise get three full runs, two of them already obsolete. This cancels the older in-flight run for the same PR. Note this is exactly the cancellation that `always()` would have defeated — see §3.

---

## 6. The merge gate (the part that is *not* in a file)

GitHub does not read branch protection from the repo. It is a repo setting, configured once in the UI.

> **Order matters:** the check names only appear in the picker after the workflow has run at least once. So: get this workflow onto `master` first, let one PR run it, *then* configure the ruleset.

**Settings → Rules → Rulesets → New ruleset → New branch ruleset**

1. **Name**: `Protect master`
2. **Enforcement status**: `Active`
3. **Target branches** → *Add target* → *Include default branch*
4. Tick **Require a pull request before merging**
5. Tick **Require status checks to pass** → *Add checks* → search for and add:
   - `Test`
   - `Build`

Those two strings are the `name:` values of the jobs. If a job has no `name:`, the check is named after the job id instead.

**Step 4 is not optional.** Required status checks only apply to merges made *through a PR*. Without "Require a pull request before merging", anyone can `git push` straight to `master` and bypass both checks entirely.

### One toggle to think about

**Require branches to be up to date before merging** forces a PR to be rebased onto the latest `master` before it can merge, re-running both checks. It is the honest setting — it catches "two PRs are each green alone but break when combined" — but it means a lot of re-running. Leave it off while learning.

---

## What I left out, and when to add it

| Skipped | Add it when |
|---|---|
| Uploading the `.trx` file as an artifact | you want to download raw results, or feed them to a reporter action |
| `dorny/test-reporter` (per-test detail as its own check run) | the one-line counters stop being enough. Costs a third-party dependency and `checks: write` |
| A `dotnet format` lint gate | you want the existing `dotnet-ci.yml` lint job required too — add `lint` to the same ruleset |
| A matrix over frameworks | the project goes back to multi-targeting. It costs a second SDK in `setup-dotnet` and splits the check into `Test (net8.0)` / `Test (net9.0)` — two brittle names to keep in sync with the ruleset |
| An aggregate `ci-passed` job | you *do* adopt matrices. Then require the single aggregate job instead of every matrix leg by name |
| A `push:` trigger on `master` | you want a status badge or a post-merge signal on `master` itself |

## Try it

```bash
git checkout -b try-pr-checks
git commit --allow-empty -m "Test the PR checks workflow"
git push -u origin try-pr-checks
```

Open the PR and watch both checks appear. Then break a test on purpose — change `Assert.Equal(5, Math.Add(2, 3));` to `Assert.Equal(6, ...)` in `dotnet/tests/Calculator.Tests/MathTests.cs` — push, and confirm three things: **Test** goes red, **Build** still runs and goes green, and the merge button stays locked.
