# How the work is run

The owner, the sessions the owner talks with, the workers they start and the reviewer that merges. The index of subjects is [README.md](README.md);
each subject is `planning/<SLUG>.md`. Machine resources, build slots and backups for a session with several workers are in `docs/agent-workflow.md`;
the subagent definitions are `.claude/agents/worker.md` and `.claude/agents/reviewer.md`.

## Who does what

### The owner

The person. The owner:

- answers proposed decisions, in any session;
- sets the order of subjects and their status in the index, and says which session takes which subject (the Assigned column);
- decides concurrency: the owner avoids two sessions running workers on the same subject, and two reviewers running at the same moment. Nothing
  enforces it; an overlap costs rework, not damage, because every change reaches `main` through the reviewer one pull request at a time;
- configures branch protection and the merge queue on GitHub.

### A session

Any conversation the owner chats with. Several may run at once, on any machine. A session:

- talks with the owner, designs, and writes and changes planning: subject files, proposed decisions, open designs (on a `planning/<topic>` branch when
  the change is not part of a subject's task);
- starts workers and reviewers, and brings their results, defaults and questions to the owner;
- **does no product code work**: it writes no product code, fixes no tests and resolves no merge conflicts. A failing test or a conflict goes to a worker
  (in a subject's code) or to the reviewer (a mechanical conflict while combining).

One session may run several workers on separate tasks of one subject. When two tasks change the same code, the session starts the second worker only
after the first is ready, or gives both tasks to one worker; that is a planning decision the session makes and tells the owner. If the workers of a
subject keep colliding, the subject is split into two subjects (the owner's call); no new role is added.

### A worker

A subagent (`.claude/agents/worker.md`) limited to one subject, often one task. A worker:

- before it builds, checks its task against the code: work the task plans that is already built is reported to the session (a task that re-plans
  built work goes to the owner as a proposed decision, never silently dropped or rebuilt);
- builds its tasks on its branch `backup/<slug>-<n>` and keeps the task's wip file (below);
- reports its defaults and questions back to the session that started it, and **never decides**: a default it takes is a proposal until the owner answers;
- edits only its subject file and the code and docs its tasks need; it never edits another subject's file and never edits `planning/README.md`;
- starts each commit message with the task id (`INSTALLER-3: ...`), reads `docs/ci-lessons.md` before it pushes and runs the repository checks;
- merges `main` into its branch whenever `main` moves; it never rewrites a pushed branch (no amend, squash, rebase or reset of pushed commits).

### The reviewer

A subagent (`.claude/agents/reviewer.md`) that combines and merges. It builds no features. It works in two steps:

1. **Combines** the ready task branches of a subject into `work/<slug>`, one at a time, building and running the affected tests after each merge.
2. **Merges** the pull request from `work/<slug>` into `main` (it opens or updates it; the title starts with the slug), one pull request at a time. With
   branch protection and the merge queue on, they prove "merges cleanly and CI is green on the combined result"; until they are on, the reviewer merges
   `main` into `work/<slug>`, waits for CI to be green, then merges.

Besides, it:

- checks consistency across subjects and product docs (the checklist below);
- carries "Needs from other subjects" into the target subject file (a task, or a line on an existing task: the only edit it makes to another subject's
  tasks) and tells the session;
- records the owner's exact answer under a proposal in the subject file (`Answer from the owner, <date>: ...`) when the owner answered outside that
  subject's session;
- edits `planning/README.md` only for order, status and assignment, and only on the owner's word; it never writes a judgment there (such as
  "overtaken"), and removes a subject's line when the pull request that deletes the subject file merges;
- fixes mechanical conflicts only (both sides' entries in a shared list, a moved line); a conflict between two designs goes back to the session as a
  question for the owner.

## Branches

| Branch | Who | For |
| --- | --- | --- |
| `backup/<slug>-<n>` (`backup/installer-3`: the slug in lower case, then the task number) | a worker | one task in progress, pushed often as backup and handover |
| `work/<slug>` (`work/installer`) | the reviewer | the subject's ready task branches combined; the pull request into `main`, whose title starts with the slug |
| `planning/<topic>` | a session | planning and design changes outside a subject's tasks |

A cloud session or worker whose harness started it on a branch of another name switches to the standard name before its first push and keeps pushing there.
One worker on a subject takes the same path: its ready branch is merged into `work/<slug>` once, and that is the pull request.

A task branch is **ready** when its head has no wip file for that task. When the pull request of `work/<slug>` merges, the `backup/<slug>-<n>` branches of
the tasks its commits name are deleted (`docs/branch-cleanup.md`); `work/<slug>` itself goes with the pull request.

## The wip file

Work in progress lives in `planning/wip/<SLUG>/<SLUG>-<n>.md` on the task's branch, never in the subject file or a product doc: what is built so far and
what is not; each open question; each default the worker took, each with an example (what a person does and what happens). The branch is pushed at every
stopping point, so the branch and its wip file are the handover when a session or a machine goes away: the next worker checks out the branch and reads
the file.

Before reporting ready, the worker resolves the wip file on the same branch, item by item, and deletes it:

- finished work: the task leaves the subject file's table; a task done in part is rewritten there to say exactly what is left;
- a question the owner answered: the answer is written into the product doc that describes that part, as design (how it works, why, an example), in a
  commit that names the proposal id and says `answered by the owner: <the answer>`;
- a question the owner has not answered, and every default: a proposed decision `<SLUG>-P<n>` in the subject file, written as a concrete scenario with
  the options and a recommendation, marked as awaiting the owner (or as a default awaiting the owner);
- a need from another subject: a line under "Needs from other subjects" naming that subject's task id.

A pull request into `main` cannot merge while any file exists under `planning/wip/` (`scripts/check_no_wip.py`, run by CI on pull requests into `main` and
on pushes to `main`, not on other branches).

## Flows

**One worker on a subject.** The session starts a worker on a task. The worker creates `backup/<slug>-<n>` from `main`, builds, commits with the task id,
pushes at every stopping point, keeps the wip file, and reports defaults and questions to the session, which takes them to the owner. When the task is
done, the worker resolves the wip file and reports the branch ready. When the owner says so, a reviewer merges the branch into `work/<slug>`, builds and
tests, opens the pull request into `main` and merges it.

**Several workers on one subject.** The session starts one worker per task, on separate tasks that do not change the same code (otherwise the second waits
for the first to be ready, or one worker takes both). Each has its own `backup/<slug>-<n>`. As branches become ready, the reviewer merges them into
`work/<slug>` one at a time, building and testing after each; it may merge the pull request with the ready tasks so far and open the next one for the rest.

**Two subjects changing the same shared code.** The second pull request to merge merges `main` and adapts. A conflict between the two designs goes to the
owner as a proposed decision in the subject that came second.

## Proposed decisions and answers

1. An open question or a default a worker took goes into its wip file, and back to the session that started it.
2. If the owner has not answered when the task is ready, it becomes a proposed decision `<SLUG>-P<n>` in the subject file.
3. The owner may answer anywhere: in the subject's session or in another one. Whoever hears the answer outside the subject's session (the reviewer, or
   the session) writes the owner's exact words under the proposal: `Answer from the owner, <date>: ...`. The next session or worker for that subject acts
   on it.
4. Acting on it: the proposal is removed from the subject file and the outcome is written into the product documentation as how the product works (what
   it does, why, an example), in the doc that describes that part, with no date and no attribution. The commit names the proposal id and says
   `answered by the owner: <the answer>`.

A subject file never holds decided content beyond its goal, its tasks, its open designs and recorded answers waiting to be acted on; a product doc never
holds a worker's default.

## Open designs

A design that is not yet tasks is an open design: a section `## Open designs` of a subject file with no task ids, one heading per design, each saying what,
why and the open questions. A subject that holds only open designs has the status "open design". When a design is settled, a session turns it into tasks.

Nothing is dropped from planning without the owner saying so: a design or a task judged overtaken goes to the owner as a proposed decision first. A commit
that removes design text from planning or from a doc says in its message where it went (into a product doc, into another subject, or dropped on the
owner's word), and the reviewer checks it the way it checks a removed proposal.

Parked work keeps its design. A line under "Parked" in the index is one line; where the parked work has real design (what it does, how, why), that
design lives in an open-design subject (status "open design", or listed from the parked line) or in a product doc, and the parked line names where.

## Statuses in the index

The index states order, status, assignment and a goal, never a judgment: a line never says that something is overtaken, obsolete or dropped unless it
cites the owner's words (`dropped by the owner, <date>`); otherwise the question is a proposed decision in a subject first. Whoever edits it (a session
or the reviewer) does so only on the owner's word.

| Status | Means |
| --- | --- |
| active | workers are on it |
| next | the next to start |
| queued | in the order, not started |
| parked | not scheduled (a line under "Parked") |
| needs review | written for the owner to accept or change before work starts |
| open design | only open designs, no tasks yet |

## The reviewer's checklist

Before it merges a pull request into `main`, the reviewer checks:

- no file under `planning/wip/` (`python3 -I scripts/check_no_wip.py`);
- CI green on the combined result (`main` merged into `work/<slug>`, or the merge queue);
- consistency across subjects and product docs: shared files, cross references, a change that contradicts another subject's design or what a product doc
  says the product does;
- every proposal removed from a subject file is backed by a recorded owner answer (`Answer from the owner, ...` in the subject file or what the owner said
  in the session, cited) and a commit `answered by the owner: ...`; no worker default reached a product doc or the subject's tasks as settled; new
  proposals and defaults are named to the session;
- every removed piece of design text says, in its commit message, where it went;
- shared files only appended to (below);
- `planning/README.md` changed only for order, status and assignment, on the owner's word.

## Shared files

`docs/ci-lessons.md`, `docs/formats.json`, `docs/ui-editors.md`, `docs/ui-coverage.md`, `docs/release-notes-pending.md`, `Directory.Packages.props`,
`DbDataKit.slnx` and the check baselines under `scripts/` are edited by **adding entries** (one per line or block, kept sorted where the file allows),
never by rewriting other entries. The `Counts:` line of `docs/ui-editors.md` is recomputed (`scripts/check_ui_editors.py` prints it). Release notes are
never cut.

## What a deletion must do

A task that deletes code lists the tasks and documents the deletion voids (in its row) and updates them in the same pull request: another subject's
task through "Needs from other subjects", a product doc and `docs/ui-editors.md` directly.

## Code comments

A comment describes the code it sits on and never cites a plan, a task, a proposal or a subject; it may point at a finished doc as
`Design: docs/<path>.md#<anchor>`. The rule is `docs/code-comments.md`.

## Old plan ids

Source comments and docs cite ids of the deleted plans (`plan 33 SC23`, `28c E17`). They are not task ids, and nothing new cites them:
CODECLEANUP-6 adds a check that refuses new ones, and CODECLEANUP-7 to CODECLEANUP-11 remove the existing ones with the comments that are out of date.
Git keeps the plans (the old plans folder before the commit that created `planning/`).
