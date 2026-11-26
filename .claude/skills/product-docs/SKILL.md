---
name: product-docs
description: >-
  How to write DbDataKit's product documentation (docs/, the READMEs, a product's docs folder) and the user-facing text of planning subjects. Use it
  before writing or changing any of them: a new page, a section, a README line, the doc text a task or an owner's answer adds, the "Planned
  architecture changes" section. It says what a reader needs, what never goes in, and how to check a sentence before committing it.
---

# Writing product docs

The docs say what the product does and how to use it, for an administrator, an author or a developer of the suite. They are read to get something
done. Every sentence has to help with that.

## Write

- **Direct, declarative and short.** State what is true: "The Runner runs each task in its own process." Not "tasks are designed to be run in
  isolation", not "it is worth noting that".
- **Conditions as an outline of scenarios.** When something depends on a condition, list the scenarios, each one naming the condition and then what
  applies, instead of a sentence full of "unless", "except when" and "otherwise":

  ```
  Where the console keeps its state:
  - No engine is named: a SQLite file, `state.db`, in the console's folder.
  - `--state-engine postgres` and `--state-connection` are given: that PostgreSQL database.
  - `settings.yaml` has a `state:` section and no flag is given: what the section names.
  ```

- **Examples, whenever one applies.** A command with its output, a file with its contents, a screen with what to click. A reader copies an example
  before reading the paragraph around it.
- **What it does, then how to use it, then an example** with real names, commands, files and values: `dbdatakit user invite --role admin --email
  ana@example.com` prints a link that works once.
- **Who does what.** Name the person, the service or the program that acts: "the Runner runs the task in its own process", not "tasks are isolated".
- **The reader's choices, and why they would make each one.** When something can be set up in more than one way, say what the reader can do and the
  reason to pick each way, then name the command or file that does it. People install, configure and choose. A program does not "install on its
  own". Wrong: "Each service installs on its own, or all four on one machine with `dbdatakit-full`." Right: "All four services can be installed
  together on a single server, or on separate servers for greater control of security and load. `dbdatakit-full` installs all four on one server."
- **One fact per sentence**, in the words the reader would use. Short sentences. If a sentence needs a second reading, rewrite it.
- **Why, when the reader needs it to act**, said once and concretely: "so a crash of one task does not stop the others".
- **Limits and failures**: what is not supported, what error the reader will see and what to do about it.

## Never write

- **Decisions as decisions.** No decision tables, no "Decided (owner, date)", no "it was decided", no proposal or task ids, no plan numbers. A
  decision the owner made is written as how the product works. The record of who decided what is planning and commit messages, never docs.
- **Slogans and maxims.** A sentence that sounds like a principle and tells the reader nothing to do. Wrong: "A command line is a tool for people, kept
  only while it earns its maintenance; what the product does is an operation." Right: "We are removing the old command-line tools. When that is done,
  the programs only run and configure the four services."
- **A sentence that turns around halfway.** Do not start in one direction and reverse it at the end. A verb followed by "nothing", "none" or "no"
  does this: "The Gateway runs nothing scheduled" first tells the reader the Gateway runs something. Put the negation before the verb, or say what
  happens instead. Wrong: "runs nothing scheduled", "holds no login", "calls none of the services". Right: "Scheduled work never runs on the
  Gateway", "does not hold a login to customer data", "never calls another service".
- **A definition by contrast.** "X, not Y" makes the reader hold the wrong idea before the right one. Say what is true. Wrong: "Work is placed by
  the logins it needs, not by the product it belongs to." Right: "Each task runs on the service that holds the logins it needs."
- **Two services in one statement**, unless every word is true of both. Wrong: "The control service and the Workspace service: authoring,
  approvals, publishing, binding." (The Workspace service never publishes or binds.) Write one statement per service.
- **Abstract nouns doing the work.** "surface", "pipeline", "posture", "story", "earns", "first-class", "single source of truth", "seamless". Say the
  thing: which screen, which command, which file.
- **Notes instead of sentences.** Every sentence has a subject, a verb and the small words ("the", "a", "is") a person would say. Wrong: "Groups
  through the token; pins both ways; a Runner joined to its group." Right: "When a service joins, the console hands it the group's key. The service
  and the console then each remember the other's certificate."
- **A list of names packed into a sentence.** The reader has to unpack the list before reaching the point. Name the group, and list the members only
  if the reader needs them. Wrong: "Set up for development, Test, Describe, Preview rows and Try, Build and stage, and a script's Test all work in
  `prod` as in any other channel." Right: "Every feature of the Workspace screens works in `prod` as in any other channel."
- **A fragment where a sentence belongs.** A list item, a table cell or a heading's first line is still a sentence when it states something. Wrong:
  "Refusing a feature in `prod`, or hiding it there." Right: "The suite does not refuse or hide a feature in `prod`."
- **A rule written so it reads as its opposite.** Under a heading such as "Rules the suite does not add", an item that opens by describing the rule
  reads as if the suite had it. Say what the suite does first. Wrong: "Leaving `prod` out of a feature, or refusing it there: ..." Right: "The suite
  does not refuse or hide a feature in `prod`. ..."
- **The writer's reasoning or the doc talking about itself.** No "this section explains", "note that", "it is important to", "in other words".
- **Agent-speak.** No sentence a person would not say out loud to a colleague. Read it aloud; if it sounds like a model talking to itself, it is wrong.

## Planned changes

A change to the architecture that is planned or in progress goes in the section **"Planned architecture changes"** of `docs/architecture.md`; another
planned change readers must know about goes in the same kind of section on the page it belongs to. Each entry starts with **Planned** or **In
progress** and says, in plain sentences, what is being removed or added, what is deprecated and what replaces it. Nothing planned is written as if
it were built. It may link the planning subject.
As the tasks land, the section gets shorter and the description of what is built gets longer. Example:

```
## Planned architecture changes

- We are removing the old command-line tools. When that is done, the suite's programs only run and configure the four services.
- `ui mcp` is being removed from every program. Agents will connect to the MCP endpoints of the console and the Gateway.
```

## The review date

The one date a doc carries is its last full review, as an HTML comment on the line under the title, which readers do not see:

```
# File formats
<!-- reviewed: 2026-11-20 -->
```

It is the date the whole file was last checked against the reference docs (`docs/architecture.md` and the others the owner approved) and against this
skill, and the owner approved the result. A later change to part of the file does not change it. A file without the line has not been reviewed. The
same line goes in a planning subject file under its title.

## Check before committing

For each sentence you added or changed:

1. What would a reader do differently after reading it? If nothing, cut it.
2. Could a reader check it against the product (a screen, a command, a file)? If not, make it concrete or cut it.
3. Would a colleague say it in a meeting, in those words? If not, rewrite it.
4. Does it name a decision, an owner, a date, a task, a plan or a proposal? Move that to planning or the commit message.

Code comments have their own rules: [docs/code-comments.md](../../../docs/code-comments.md).
