# Contributing to Reservoir Labs

This is a two-person bachelor thesis project. The rules here exist to keep our branches clean, ensure both authors review each other's work, and produce a defensible audit trail for the thesis.

## Branches

- `main` is release-only. The only merges to `main` come from `develop` at thesis-submission milestones.
- `develop` is the working branch. All feature branches merge here.
- Both `main` and `develop` are protected: PR required, linear history, no force pushes.

## Branching

- Create one branch per Linear issue. Branch off `develop`.
- Branch name format: `feature/fix/bug<=(based on the issue characteristic)/DOG-N-short-description` (lowercase, kebab-case).
  - Example: `feature/DOG-15-implement-order-service`
- Keep branches short-lived — merge within a few days, not weeks.

## Commits

- Conventional commit messages see: https://www.conventionalcommits.org/en/v1.0.0/
- Every commit message ends with the Linear issue ID
- Format: `feat: short description (DOG-N)`
  - Example: `feat: implement POST /orders endpoint (DOG-15)`
- One commit per logical change. Don't bundle unrelated changes into one commit.
- Keep the subject line under 72 characters. Add a body if the _why_ needs explanation.

## Pull requests

- Open a PR from your branch to `develop`. PR title format: `feat: short description`.
- Linear's GitHub integration auto-links the PR to the issue when the title contains the ID.
- The other author reviews and approves before merge. self-merging if needed.
- Merge strategy: **squash and merge** (preserves linear history, keeps history readable).
- Delete the branch after merging.

## Code review

- Both reviewer and author act in good faith. The reviewer's job is to catch bugs, suggest improvements, and confirm the change matches the Linear issue's scope.
- Approve only if you've actually read the diff. "LGTM" without reading helps no one and risks plagiarism flags or sloppy code in the final thesis.
- If a change is out of scope for the issue, ask the author to split it into a separate PR.

## Releases to `main`

- Release = merging `develop` into `main`. Done at thesis-submission milestones.
- Tag the release with the milestone (e.g., `thesis-draft-v1`, `thesis-submitted-2026-07-XX`).
- No direct work on `main`. Ever.

## What goes where

- Code: under `/services/<service>` or `/dashboard`.
- Architecture decisions: `/docs/adr/ADR-NNN-short-name.md`.
- Architecture reference: `/docs/arch/ARCH-NNN-short-name.md`.
- Research summaries: `/docs/research/<topic>-summary.md`.
- Experiment plans and results: `/docs/experiments/`.
- Thesis chapters: `/docs/thesis/`.
- KIU paperwork, supervisor correspondence: `/docs/admin/`.

## When something breaks

- If you break `develop` (CI red, build broken, etc.), fixing it is your top priority before any new work.
- If you can't fix quickly, revert the offending PR rather than leaving `develop` broken.
