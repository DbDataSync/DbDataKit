# How the work is run

Three roles: the owner, one subject agent per subject, and one coordinator. The index of subjects is [README.md](README.md); each subject is
`planning/<SLUG>.md`. Machine resources, build slots and backups for a session with several agents are in `docs/agent-workflow.md`.

## The owner

- Answers proposed decisions.
- Sets the order of subjects, their status (active, next, queued, parked, or needs review: a subject written for the owner to accept or change before any work starts) and their priority.
- Assigns a subject to a session or a machine (the Assigned column of the index).
- Configures branch protection and the merge queue on GitHub.

## A subject agent

One per subject; it may use subagents of its own.

- **Edits only its subject file** and the code and docs its tasks need. It never edits another subject's file and never edits `planning/README.md`.
- **Works on its own branch.** In a cloud session the harness names the branch; the pull request's title starts with the slug (`INSTALLER: ...`).
- **Keeps pull requests small:** one task, or a few that belong together. Each commit message starts with the task id (`INSTALLER-3: ...`).
- **Merges `main` into its branch** before it opens a pull request and again whenever `main` moves under an open one. It does not rebase a pushed branch.
- **Writes work in progress outside the subject file**, in `planning/wip/<SLUG>/<TASK-ID>.md`, one file per task: what is built so far and what is not;
  each default it took, as a concrete scenario; each decision it needs, with options and a recommendation; anything for another subject. A branch may
  carry and push wip files. A pull request into `main` cannot merge while any file exists under `planning/wip/` (`scripts/check_no_wip.py`, run on pull
  requests into `main` and on pushes to `main`).
- **Before the pull request merges, it resolves each wip file item by item and deletes it:**
  - finished work: the task leaves the subject file's table; a task done in part is rewritten there to say exactly what is left;
  - a question the owner has not answered, or a default it took: a proposed decision in the subject file (`<SLUG>-P<n>`), written as a scenario (what a
    person does and what happens under each option) with a recommendation, marked as awaiting the owner, or as a default awaiting the owner;
  - a question the owner answered: see "Proposed decisions" below;
  - a need from another subject: a line under "Needs from other subjects" naming that subject's task id.
- **Asks for other subjects' work only through "Needs from other subjects"**, naming an existing task id. The coordinator carries it over.
- **When its last task lands**, the same pull request turns what is still useful into product documentation and deletes the subject file and its line
  in the index (the coordinator makes the index edit when it merges).
- **Reads `docs/ci-lessons.md` before it pushes** and adds a new CI lesson in the commit that fixes it.

## Proposed decisions

A proposed decision moves through these places, and only these:

1. An open question or a default an agent took while working goes into `planning/wip/<SLUG>/<TASK-ID>.md`.
2. If the owner has not answered it when the pull request is ready, it becomes a proposed decision `<SLUG>-P<n>` in the subject file.
3. When the owner answers, the proposal is removed from the subject file and the outcome is written into the product documentation as how the product
   works: what it does, why, and an example, in the doc that describes that part (not as a "decision", with no date and no attribution). The commit that
   does it names the proposal id and says `answered by the owner: <the answer>`.

A subject file never holds decided content beyond its goal and tasks; a product doc never holds an agent's default.

## The coordinator

One, and never also a subject agent. It builds no features.

- **Merges subject pull requests one at a time.** It relies on branch protection and the merge queue for "merges cleanly and CI is green on the combined
  result"; until those are on, it merges `main` into the pull request's branch and waits for CI before it merges.
- **Refuses to merge** a pull request with any file under `planning/wip/`.
- **Checks consistency across subjects and docs:** shared files, cross references, a pull request that contradicts another subject's design or how a
  product doc says the product works.
- **Checks every proposal a pull request removes from a subject file** against a commit message `answered by the owner: ...` backed by something the
  owner actually said (it cites where), and that no agent default reached a product doc or the subject file's tasks as settled. It flags a pull request
  that adds proposed decisions or defaults the owner has not seen.
- **Carries "Needs from other subjects"** into the target subject file (the only edit it makes to a subject file: a task, or a line on an existing task)
  and tells the owner.
- **Maintains `planning/README.md`**: order, status, assignment as the owner sets them; a subject's line removed when its file is deleted.
- **Resolves only mechanical conflicts** (both sides' entries in a shared list); a semantic conflict goes back to the subject agent.

## Shared files

`docs/ci-lessons.md`, `docs/formats.json`, `docs/ui-editors.md`, `docs/ui-coverage.md`, `docs/release-notes-pending.md`, `Directory.Packages.props`,
`DbDataKit.slnx` and the check baselines under `scripts/` are edited by **adding entries** (one per line or block, kept sorted where the file allows),
never by rewriting other entries. The `Counts:` line of `docs/ui-editors.md` is recomputed (`scripts/check_ui_editors.py` prints it).

## What a deletion must do

A task that deletes code lists the tasks and documents the deletion voids (in its row) and updates them in the same pull request: another subject's
task through "Needs from other subjects", a product doc and `docs/ui-editors.md` directly.

## Old plan ids

Source comments and docs cite ids of the deleted plans (`plan 33 SC23`, `28c E17`). They are not task ids, and nothing new cites them:
CODECLEANUP-6 adds a check that refuses new ones, and CODECLEANUP-7 to CODECLEANUP-11 remove the existing ones with the comments that are out of date.
Git keeps the plans (the old plans folder before the commit that created `planning/`).
