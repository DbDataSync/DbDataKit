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

- takes a new request through planning first ("A new request", under Flows): what is planned and documented, what overlaps, agreed with the owner,
  before anything changes;
- talks with the owner, designs, and writes and changes planning: subject files, proposed decisions, open designs (on a `planning/<topic>` branch when
  the change is not part of a subject's task);
- starts workers and reviewers, and brings their results, defaults and questions to the owner;
- starts every worker and reviewer with `isolation: "worktree"`, so none works in the session's checkout, and names in the brief a branch that no
  other checkout has open;
- **does no product code work**: it writes no product code, fixes no tests and resolves no merge conflicts. A failing test or a conflict goes to a worker
  (in a subject's code) or to the reviewer (a mechanical conflict while combining).

One session may run several workers on separate tasks of one subject. When two tasks change the same code, the session starts the second worker only
after the first is ready, or gives both tasks to one worker; that is a planning decision the session makes and tells the owner. If the workers of a
subject keep colliding, the subject is split into two subjects (the owner's call); no new role is added.

**Talking with the owner.** The owner reads every message, often on a phone, and answers without scrolling back. So a session:

- **keeps replies short**: the answer or the question first, then only what the owner needs to decide or check. No recap of what the owner already
  knows, no list of everything that could be said. A worker's long report is not passed on: the session gives the result in a few lines and the
  owner asks for detail where wanted. A wall of text and a pile of questions lead to bad answers, so neither is sent;
- **asks one thing at a time, then moves on**: one question per message, and no list of what else is still open. When the owner answers, the
  session acts on the answer and says in a line what it did. It asks the next question only when the work cannot go on without it; otherwise it
  waits for the owner to ask for the next item. The owner can ask for a summary of what is open at any time.
  The question stands on its own: it names the item it is about and quotes the text or gives the options, so the owner can answer it without
  looking anywhere else. Several questions go in one message only as a question dialog, each one complete;
- **keeps one thread**: while a question is waiting for the owner's answer, the session sends nothing else about another item. A worker or
  reviewer result that arrives meanwhile is held, and reported in one line after the answer is handled. A question already asked is not asked
  again, and an answer is handled before anything new is said. Items are always named with their subject (`<SLUG>-P<n>`, never `P<n>` alone);
- **re-checks every open question and default before showing it, and again after each answer**. It reads each open proposal and each default a
  worker took against what the owner has decided since the text was written (answers, settings removed or renamed, paths moved, tasks added). One
  that an answer decided, replaced or made meaningless is not shown: in the same change it is removed or rewritten, and what it still says that
  matters goes into the task or open design that now carries it. The owner is shown only what is still open, in terms of the current plan;
- **shows the exact text it proposes for docs and planning** (a paragraph of a product doc, a section of a subject file, a rule in this file or
  `CLAUDE.md`) in small batches, and applies a batch only after the owner approves it. Code is reviewed the usual way, by its tests and the
  reviewer, not shown line by line;
- **asks what the owner means** when a request could mean more than one thing, instead of guessing;
- **writes plainly**, by the rules of the skill `product-docs`: direct statements, no slogans, no internal names without saying what they are;
- **says what it changed and where it is pushed** in a line or two, when it has changed something.

**Task rows.** A subject's task rows are in priority order only: a row does not depend on the rows above it unless it says so. Every dependency is a
task id in the row's "Depends on" cell. A row's text may say why ("Follows <SLUG>-<n>: the screen that task adds exists"), but a task named after "Follows"
must also be in that cell. `scripts/check_plans.py` refuses a cycle and a "Follows" missing from the cell, and reports each dependency on a subject
ranked lower in `planning/README.md`. When a task is finished, its row leaves the subject file, and in the same commit every reference to it is cleared: its id
leaves each "Depends on" cell and each line under "Needs from other subjects" that names it, in any subject file. A line that still says something
another task relies on is rewritten to name what now exists (a library, a command, a page) instead of the task; a line with nothing left goes.
`scripts/check_plans.py` refuses a reference to a task that no longer exists.

**Review dates.** Every product doc, README and planning subject file carries the date of its last full review, `<!-- reviewed: YYYY-MM-DD -->` on the
line under its title (the skill `product-docs`, "The review date"). A session sets it when the owner approves a review of the whole file: checked
against `docs/architecture.md` and the other reference docs, and written by the skill. A partial change leaves it as it is. A new file written under
these rules and approved by the owner gets the date when it is written. `python3 -I scripts/review_dates.py` lists the files that have no date yet.

**Files only the owner changes.** These files change only with the owner's approval: `CLAUDE.md`, `planning/ROLES.md`, `docs/architecture.md`,
`.claude/skills/product-docs/SKILL.md`, `.claude/settings.json` and `.claude/hooks/protect-reference-docs.py`. Claude Code asks before writing any of
them: the "ask" rules in `.claude/settings.json` cover the file tools, and the hook covers shell commands. A session shows the proposed text first,
then the owner approves the prompt. To protect another file, add it to the ask list and to the hook's `PROTECTED` list. Not set up yet: a
`CODEOWNERS` file with GitHub's required code-owner review, which would also stop changes made outside Claude Code; it is the next step if this
proves not strong enough.

### A worker

A subagent (`.claude/agents/worker.md`) limited to one subject, often one task. A worker:

- before it builds, checks its task against the code: work the task plans that is already built is reported to the session (a task that re-plans
  built work goes to the owner as a proposed decision, never silently dropped or rebuilt);
- builds its tasks on its branch `backup/<slug>-<n>` and keeps the task's wip file (below);
- reports its defaults and questions back to the session that started it, and **never decides**: a default it takes is a proposal until the owner answers;
- edits only its subject file and the code and docs its tasks need; it edits another subject's file only to clear references to a task it finished (Task rows), and never edits `planning/README.md`;
- starts each commit message with the task id (`<SLUG>-<n>: ...`), reads `docs/ci-lessons.md` before it pushes and runs the repository checks;
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

**Planning and documentation only: straight to `main` (owner, 2026-11-18).** A branch whose changes are only planning (`planning/`) and documentation (`docs/`, READMEs and other `.md` files, the agent definitions under `.claude/`) needs no pull request: the reviewer checks it with the same checklist, merges `main` into it if `main` moved, runs the repository checks in a clean worktree of the merge (`python3 -I -m unittest discover -s scripts/tests` and the `scripts/check_*.py` of `CLAUDE.md`, "Build and test", `check_doc_inputs.py` among them), and pushes the merge to `main` without waiting for CI to be idle: such a push starts no product suite (`docs/ci-lessons.md`, "Which changes start which workflow"). It then watches only **Repository checks** and **Planning work in progress** on that commit; the product workflows do not appear for it, which is expected. Before pushing, it confirms the merge changes only those paths: `git diff --name-only origin/main...HEAD` lists nothing outside `planning/`, `.claude/`, `docs/`, the product docs folders and `*.md`; anything else is a code change. Any change to code, tests, scripts, workflows or packaging goes through a pull request into `main`, and keeps the rule above: `main` merged in, CI green on that head, then the merge.

## Branches

| Branch | Who | For |
| --- | --- | --- |
| `backup/<slug>-<n>` (`backup/installer-3`: the slug in lower case, then the task number) | a worker | one task in progress, pushed often as backup and handover |
| `work/<slug>` (`work/installer`) | the reviewer | the subject's ready task branches combined; the pull request into `main`, whose title starts with the slug |
| `planning/<topic>` | a session | planning and design changes outside a subject's tasks |

**Few branches.** Tasks done one after another by the same worker share one branch: the first task's `backup/<slug>-<n>`, with each later task's commits on top of it, each commit still starting with its own task id. A new branch is made only when workers run at the same time, one branch per worker. When such a worker finishes, the session merges its branch into the run's branch locally, pushes the result, and lists the finished branch in `scripts/branches-to-delete.txt` in the same change. A session never leaves a finished branch behind.

**Pushing.** Nothing stays only on one machine. Every commit is pushed to its own branch as soon as it is made, and before any wait, report or stop; a commit meant for `main` that has to wait (for CI to go idle, for a review) is first pushed to a `backup/<topic>` branch. Every report to a session names the branch and commit where its work is pushed, so the owner never has to ask whether something is stored.

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

**A new request.** The owner asks a session for something (a feature, a change to the repository, a change to the environment the work runs in). Before
anything changes, the session:

1. asks what the owner means when the request could mean more than one thing (which product, which users, what "working" looks like), instead of
   guessing and offering to build;
2. finds every subject in `planning/README.md` that covers or touches it, reads them, including their open designs and proposals, and reads the product
   docs they name (`docs/architecture.md` first) and, when a design seems missing, the old plans (below);
3. tells the owner what exists, what is already planned, what overlaps and what contradicts the request, citing each;
4. agrees with the owner where the work goes: tasks added to an existing subject, a subject reshaped, or a new subject (status "needs review"); a
   contradiction with a decided design is a proposed decision, never a silent change;
5. writes that planning on a branch for the owner to review, and only then starts workers.

**One subject, one place.** Planning is broken up by subject before it is committed. Each task, open design and proposal goes in the subject that
owns its work, never in the nearest subject or the one a conversation happened in. A piece of work that fits no subject goes to a new subject, not into
a catch-all. A subject's goal names what it covers and what it leaves to other subjects. When a subject gathers work that belongs elsewhere, a session
moves that work to its subject before adding anything new.

Example: while planning knowledge, a session finds work on actions, agents, connections and the CI engines. Each goes to its own subject (ACTIONS,
AGENTS, CONNECTIONS, CI) before the knowledge plan is committed.

**Architecture directions are in `docs/architecture.md`.** A subject that changes the suite's architecture (a service, a program, how the parts call each
other, where code runs, how people or agents connect) adds its direction to the section "Planned architecture changes" of `docs/architecture.md` in the
same branch as the subject, marked **Planned**. The first task that starts marks it **In progress**. Each task that lands shortens that entry and
describes what is now built in the section that covers it; the last one removes the entry. So the doc always says where the suite is and where it is
going, and a reader never takes a planned change for a built one.

A session may read anything, and build or run something only to answer a question, saying so. It changes nothing else first: no code, scripts, hooks,
`.claude/` or `.mcp.json` settings, no settings or setup script of a cloud environment, no long-running services. The tools of a harness that offer to
change an environment are used the same way: after the plan names the change and the owner has agreed it.

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
4. Acting on it: the proposal is removed from the subject file and the outcome is written where it applies: into the product documentation as how the
   product works (what it does, why, an example), in the doc that describes that part, or, while the work is not built, into the subject's tasks or open
   designs as plain design. No date and no attribution go with it. The commit names the proposal id and says `answered by the owner: <the answer>`;
   the commit message is the record of who decided what.
5. An answer is acted on at once when it can be: a session that hears an answer and writes it into the tasks in the same change removes the proposal
   then, and writes no `Answer from the owner` line. That line is only for an answer nothing has acted on yet.

A subject file never holds decided content beyond its goal, its tasks, its open designs and recorded answers waiting to be acted on; a product doc never
holds a worker's default.

## Open designs

A design that is not yet tasks is an open design: a section `## Open designs` of a subject file with no task ids, one heading per design, each saying what,
why and the open questions. A subject that holds only open designs has the status "open design". When a design is settled, a session turns it into tasks.

Nothing is dropped from planning without the owner saying so: a design or a task judged overtaken goes to the owner as a proposed decision first. A commit
that removes design text from planning or from a doc says in its message where it went (into a product doc, into another subject, or dropped on the
owner's word), and the reviewer checks it the way it checks a removed proposal.

Parked work is in `planning/PARKED.md`, one section per item, with its whole design: what it does, how, why, and why it is parked. Parked work is
never kept in a subject file, so a subject can close without it. Un-parking an item moves its section into a subject as an open design or tasks.

## Plans

A plan groups work toward one outcome. It is a file `planning/plans/<NAME>.md` with the outcome in one or two sentences and the tasks that reach it, in
order, by their ids from any subject. A task stays in its subject; a plan only lists it. A plan holds no statuses:
`python3 -I scripts/plan_status.py <NAME>` prints each task's status:

- **merged:** its row has left its subject, and a commit on `main` starts with its id;
- **in progress:** a `backup/<slug>-<n>` branch exists;
- **blocked:** a task in its "Depends on" cell is not merged;
- **not started:** otherwise;
- **unknown:** the id is in no subject and no commit on `main` names it (a task that moved without its plan being updated).

When a task moves to another subject, the move updates the plans that list it. `scripts/check_plans.py` fails when a plan names a task that is neither
in a subject nor merged. The owner decides which plans exist and their order. The session proposes a plan's outcome and its task list, and keeps the
list in order as work moves.

Example: the plan `SYNCREADY` says "A person installs the suite on Linux, authors a Sync project in the Designer, deploys it and runs it, with no step
done by hand", and lists INSTALLER-21, INSTALLER-7, SYNCAUTHORING-6, TESTING-4 and SYNC-1. The command shows the first two merged and the rest not
started.

## Statuses in the index

The index states order, status, assignment and a goal, never a judgment: a line never says that something is overtaken, obsolete or dropped unless it
cites the owner's words (`dropped by the owner, <date>`); otherwise the question is a proposed decision in a subject first. Whoever edits it (a session
or the reviewer) does so only on the owner's word.

| Status | Means |
| --- | --- |
| active | workers are on it |
| next | the next to start |
| queued | in the order, not started |
| parked | not scheduled (a section of `planning/PARKED.md`) |
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
- a subject that changes the architecture has its entry in "Planned architecture changes" of `docs/architecture.md`, marked Planned or In
  progress as its tasks stand, and a landed task shortened its entry and described what it built;
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

## The old plans (before `planning/`)

Until 2026-11-17 the open work was in the folder `docs/plans` (numbered plans such as `33-fewer-services.md`, the index `README.md` of that folder,
`open-work.md`, `needs-review`, and `RELEASE-NOTES-PENDING.md` in that folder). Commit
`127c80e89700f324624d8448c4da12d7999cd928` ("Planning: replace docs/plans with planning/ subjects, roles and the wip check") deleted them and created
`planning/`. The last commit that has the old tree is its parent, **`31e0319f9cd7108c52ad64d8f6d8f852df676c35`**:

```
git ls-tree -r --name-only 31e0319f -- docs/plans     # the old files
git show 31e0319f:<a path that listed>                # one old file
git grep -n "<words>" 31e0319f -- docs/plans          # search them
```

The audit that traced every old item to where it went (`planning/wip/MIGRATION/trace.md`, 1,699 items, and `scripts/audit_plan_migration.py`) was
deleted after the owner signed it off; the last commit that has it is the parent of `c3265139d5679542458bd9409c13afde6f0d0e8f`
(`git show c3265139^:planning/wip/MIGRATION/trace.md`). Old plan and row ids (`plan 33 SC23`, `28c E17`) found in code or docs are looked up there.

**Nobody searches the old tree unless the owner asks for it.** A session, worker or reviewer that meets a doc or a subject that seems missing or thin
says so to the owner (naming what seems missing), and searches the old tree only when the owner asks; what it finds goes back through the normal path
(a subject's task or open design, or a product doc as design).

## Old plan ids

Source comments and docs cite ids of the deleted plans (`plan 33 SC23`, `28c E17`). They are not task ids, and nothing new cites them:
CODECLEANUP-6 adds a check that refuses new ones, and CODECLEANUP-7 to CODECLEANUP-11 remove the existing ones with the comments that are out of date.
Git keeps the plans (the old plans folder before the commit that created `planning/`).
