---
name: ai-bot-pr-review
description: >-
  Fetches GitHub PR feedback from AI code review bots (e.g. Devin, CodeRabbit,
  Greptile, Codex, Cursor), triages real bugs vs false positives, applies
  minimal fixes, commits and pushes, then posts threaded replies and a summary
  on the PR. Use when the user asks to address AI PR reviews, fix bot comments,
  or resolve CodeRabbit/Devin/Greptile findings on a pull request.
---

# AI bot PR review — triage, fix, reply

## When this applies

Use for **open PRs** where **automated reviewers** left inline or top-level comments. Typical bot logins (non-exhaustive): `devin-ai-integration`, `coderabbitai`, `greptile-apps`, `chatgpt-codex-connector`, `cursor`.

Human reviews can follow the same workflow; prioritize **actionable, file-scoped** bot threads first.

## Preconditions

- Repository is a **git** checkout with `origin` pointing at GitHub.
- **GitHub CLI** (`gh`) is installed and authenticated (`gh auth status`).
- User intent: **implement fixes** (not “explain only”). Run commands in the repo; do not only suggest commands.

## Workflow

### 1. Resolve PR and repo

- If the user gave a PR URL or number, use it.
- Otherwise: `gh pr view --json number,url --jq .` from the current branch, or ask once.

Record: `OWNER/REPO`, PR number `N`, head branch name.

### 2. Collect all bot feedback

Run **both**; inline comments carry the real line context:

```bash
# File/line review comments (all authors — filter by bot login in analysis)
gh api "repos/OWNER/REPO/pulls/N/comments" --paginate

# Review summaries (optional bodies)
gh pr view N -R OWNER/REPO --json reviews

# Top-level PR conversation
gh api "repos/OWNER/REPO/issues/N/comments" --paginate
```

Filter threads where `user.login` matches known bots **or** the body contains vendor markers (e.g. `devin-review`, `coderabbit`, `greptile`).

Skip a thread if a human already replied with **resolved** / **won’t fix** and the code matches that outcome.

### 3. Triage each finding

| Signal | Action |
|--------|--------|
| Thread says resolved and code matches | Skip |
| Bot misread diff or suggestion is wrong | **False positive** — reply with concise reasoning; no code change |
| Valid defect, missing edge case, inconsistency | **Fix** — minimal diff in cited files |
| Style-only / nit with no project rule | Optional: reply “acknowledged, out of scope” or skip |

Read **full function or module context** around the cited lines before editing.

### 4. Implement fixes

- One logical commit (or a small series): scope to review-driven changes only; **do not** mix unrelated refactors.
- Prefer matching existing project patterns (naming, error handling, tests).
- Run project-appropriate checks before commit (e.g. `pytest`, `npm run lint`, `ruff`) — **0 new errors** before push.

### 5. Commit and push

- Commit message: clear imperative summary; include `[Cursor]` if that is the repository’s convention.
- Push the PR head branch: `git push origin HEAD` (or `git push -u origin <branch>` when setting upstream).

### 6. Reply on GitHub (required)

**A. Inline reply** (continues the review thread)

`gh -f in_reply_to=` sends a **string**, which GitHub rejects (422). Post JSON with a **numeric** `in_reply_to`:

```bash
jq -n --arg b '✅ **Resolved** in `COMMIT_SHA`. Brief what changed.' \
  '{body: $b, in_reply_to: PARENT_COMMENT_ID}' \
  | gh api --method POST "repos/OWNER/REPO/pulls/N/comments" --input -
```

Threaded replies are **pull review comments**: they show under **Files changed** on the matching thread, not as prominent entries on the **Conversation** tab alone. After new commits, some replies may anchor to an older SHA (`line` null) — users should expand **outdated** discussions or open each `#discussion_r…` URL directly.

**B. Post or repair direct threaded replies for each addressed finding** (required)

After fixes are pushed, ensure each addressed review comment has a direct threaded reply from you. If your reply already exists but is stale or incomplete, edit it in place.

Resolve your GitHub login once (for filtering self-authored replies):

```bash
YOUR_GH_LOGIN=$(gh api user -q .login)
```

```bash
# 1) Pull all PR review comments
COMMENTS_JSON=$(gh api "repos/OWNER/REPO/pulls/N/comments" --paginate)

# 2) Parent review comment ID to reply under (numeric)
PID=12345678

#    - find existing self-authored reply in same thread
EXISTING_REPLY_ID=$(jq -r --arg u "$YOUR_GH_LOGIN" --argjson pid "$PID" '
  .[] | select(.in_reply_to_id == $pid and .user.login == $u) | .id
' <<<"$COMMENTS_JSON" | head -n1)

if [ -n "$EXISTING_REPLY_ID" ] && [ "$EXISTING_REPLY_ID" != "null" ]; then
  # Repair/update existing threaded reply
  jq -n --arg b '✅ **Resolved** in `COMMIT_SHA`. Brief what changed.' '{body:$b}' \
    | gh api --method PATCH "repos/OWNER/REPO/pulls/comments/$EXISTING_REPLY_ID" --input -
else
  # Post new direct threaded reply (in_reply_to must be a JSON number)
  jq -n --arg b '✅ **Resolved** in `COMMIT_SHA`. Brief what changed.' --argjson pid "$PID" \
    '{body:$b, in_reply_to: $pid}' \
    | gh api --method POST "repos/OWNER/REPO/pulls/N/comments" --input -
fi
```

Set `PID` to the bot’s (parent) review comment id for each thread you address.

Always include the fix commit SHA and a one-line rationale in each threaded reply.

**C. Summary comment** on the PR (required, post this after A/B)

```bash
gh pr comment N -R OWNER/REPO --body-file path/to/summary.md
```

The summary must include a concise list of what was fixed, what was marked false positive, and which threads were skipped (with reasons).

**D. Link index for operators** (recommended with A/B)

Add a **Conversation** comment (`gh pr comment`) with a markdown table of `https://github.com/OWNER/REPO/pull/N#discussion_rCOMMENT_ID` links for each reply so reviewers see them without hunting the diff.

### 7. Shell pitfalls when posting comments

- **Do not** pass markdown that contains `$Variable` inside **double-quoted** `--body "..."` in bash — the shell will expand it.
- **Safe patterns**: `--body-file summary.md`, or `jq -n --rawfile b file.md '{body: $b}' | gh api ... --input -` for PATCH/POST JSON bodies.

### 8. Report back to the user

Always end with a **short table**:

| Bot / source | Topic | Result |
|--------------|-------|--------|
| … | … | Fixed in `abc1234` / False positive — … / Skipped — … |

Plus links to the PR and any important reply threads.

## Commit scope discipline

- Stage **only** files changed for the review; avoid `git add -A` if other WIP exists.
- If the working tree has unrelated edits, **do not** include them in the review-fix commit without explicit approval.

## If `gh` cannot post

- Confirm `gh auth status` and SSO authorization for the org.
- User may need to post replies manually; still deliver the exact markdown bodies in the chat.
