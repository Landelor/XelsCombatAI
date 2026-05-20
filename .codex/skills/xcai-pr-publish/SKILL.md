---
name: xcai-pr-publish
description: Use when publishing XelsCombatAI local changes to GitHub, committing direct-main solo/agent work, creating a branch when review helps, or fixing validation failures caused by Conventional Commit rules.
---

# XCAI Publish

Use this skill for the XelsCombatAI publish flow after implementation work is complete: inspect the dirty tree, choose direct-main or branch publication, commit with a valid Conventional Commit subject, push, and verify GitHub checks.

## Required Rules

- Read root `AGENTS.md` first when it has not already been loaded for the current task.
- Use the GitHub plugin skill `github:gh-fix-ci` when investigating failing GitHub Actions checks. Use `github:yeet` only when the user explicitly wants a PR.
- Commit subjects must match:

```text
^[a-z]+(\([^)]+\))?!?: .+
```

- Use a commit headline such as `fix: improve bmr mechanic positioning` or `feat(positioning): add mechanic exit hints`. Do not prefix it with `[codex]`.
- Direct commits to `main` are allowed for solo/agent work when appropriate. Use a branch named `codex/{short-description}` when review, staging, or mixed local work makes that safer.
- If opening a PR, keep it draft unless the user explicitly asks for ready for review, and use the Conventional Commit headline as the PR title.
- Do not stage unrelated files silently. If the dirty tree is mixed, stage explicit paths or ask for scope.

## Publish Workflow

1. Confirm tooling and context:

```bash
gh --version
gh auth status
git status -sb
git branch --show-current
git remote get-url origin
gh repo view --json nameWithOwner,defaultBranchRef
```

2. Inspect scope before staging:

```bash
git diff --stat
git diff --name-status
```

3. Decide publication mode:
   - Stay on `main` for direct solo/agent work when the dirty tree is scoped and validation can run locally.
   - Create `codex/{short-description}` if review, staging, or mixed local work makes a branch safer.
4. Stage only intended files. Prefer explicit paths unless the user clearly asked for the full dirty tree.
5. Commit with a terse Conventional Commit message. The PR title, if any, should match the commit headline.
6. Run or cite the most relevant validation already run after the final edits. For broad XCAI changes, prefer:

```bash
scripts/test-and-build.sh
dotnet format XelsCombatAI/XelsCombatAI.csproj --verify-no-changes
dotnet format tools/FightReview.Tests/FightReview.Tests.csproj --verify-no-changes
git diff --check
```

7. Push with tracking:

```bash
git push -u origin "$(git branch --show-current)"
```

8. Open a draft PR only when the user asked for a PR or the chosen publication mode requires review. If using `gh`, pass an explicit conventional title:

```bash
gh pr create --draft --base main --head "$(git branch --show-current)" --title "fix: concise summary" --body-file /tmp/xcai-pr-body.md
```

9. Check push or PR status:

```bash
gh pr checks <pr-number> --repo XelsPlugins/XelsCombatAI --watch
```

## Fixing Commit-Gate Failures

If validation fails because a local commit subject does not follow Conventional Commit format, amend the commit subject first:

```bash
git commit --amend -m "fix: concise summary"
git push --force-with-lease
```

If a PR title gate is also present, update the PR title to match the commit headline:

```bash
gh pr edit <pr-number> --repo XelsPlugins/XelsCombatAI --title "fix: concise summary"
```

Changing only a title may not trigger a new validation run. If checks remain attached to the old failed run, create a no-content commit update and push with lease:

```bash
git commit --amend --no-edit
git push --force-with-lease
gh pr checks <pr-number> --repo XelsPlugins/XelsCombatAI --watch
```

Use `--force-with-lease`, not an unconditional force push.

## Final Response

Include the branch, current commit, push/check results, and PR URL/status if a PR was opened. For combat behavior changes, include the root `AGENTS.md` Purpose fit note.
