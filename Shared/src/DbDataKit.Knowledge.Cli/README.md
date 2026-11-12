# DbDataKit.Knowledge.Cli

Knowledge as commands and as a published kind (plan 27a KN10, KN16, KN17). It builds on `DbDataKit.Knowledge` (the item format, scope, chunking), `DbDataKit.Knowledge.Index` (the index on a state database) and `DbDataKit.Agents` (the budget of what an agent is always given).

## The checks (`KnowledgeChecks`)

`KnowledgeChecks.Check(project, sourceName, catalog, ownerExists, agents)` is what `knowledge check` prints and what a publish runs first. Every diagnostic has a file and a line:

- the strict reader's own (`DBD-KNW-001` to `-006`: a bad value, the format number, the front matter, a warning for an instruction with no owner);
- every subject against the catalog (`DBD-KNW-010`, at the subject's own line);
- the owner of an item against the groups of the identity home (`DBD-KNW-030`);
- for every agent the project reaches, the budget: `DBD-KNW-040` is an error that names the agent and the largest item the project adds, `DBD-KNW-041` a warning when the agent was over before the project added anything. An instruction, a guardrail and a reference in the agent's `knowledge.always` count; a reference found by search never does. The number is what `ModelSpace` counts (a fifth of the model's window by default; the estimate of plan 27 SD15).

The host says what each agent is given besides knowledge (`AgentBudgetInput`: the tokens of its instructions, tool definitions and skill descriptions, its model's window, the other knowledge it draws on).

## The kind `knowledge-project` (`KnowledgeDefinitions`)

A kind of plan 30's registry for a serving target. A version is the manifest `knowledge-project.yml` (`knowledge-project: 1`, `name`, optionally `source: install|project`, the name agents give it in `knowledge.from`) and every Markdown file and `_knowledge.yml` of the folder, each an artifact kept by its hash. Staged by default: the serving layer's module checks and indexes a version at stage and switches at activation. `KnowledgeDefinitions.Publish` runs the checks and refuses the whole publish when any is an error (`KnowledgePublishRefusedException` carries the diagnostics); the same files again change nothing. `KnowledgeProject.LoadFiles` reads the files of a stored version the way the folder is read.

## The noun `knowledge` (`KnowledgeNoun`)

`KnowledgeHost` is what a host supplies; a verb whose piece is missing is not offered.

| Verb | Does | Needs of the host |
| --- | --- | --- |
| `check [<folder>]` | the checks above for the host's projects or a folder | projects, catalog, agents |
| `context <agent> [--in-play <subject> ...]` | what the agent would be given, in order, with each part's tokens against its budget | `Context` |
| `orphans` | items whose subjects are all gone | catalog |
| `undescribed` | measures, dimensions and attributes with no description | `SemanticCatalogs` |
| `index status\|rebuild [--channel]` | model, counts and progress per channel; a rebuild needs the administrator role | `Index` |
| `search <text> [--about] [--kind] [--channel]` | words, meaning or both, as the caller sees it; at most three chunks per item, adjacent ones joined, each with title, heading path, source and version | `Reader` |
| `show <id> [--section] [--version] [--channel]` | one item, or one whole section of it | `Reader` |
| `about <subject>` | items about a subject or anything inside it | `Reader` |
| `publish <folder> [--to] [--name]` | checks and publishes one version | `Publish` |

`StateKnowledgeReader` is the reader over a `StateKnowledgeIndex` per channel under the access filter of `KnowledgeAccess`: an item the caller may not see is not found, the same answer as one that is not there. These commands replace `catalog search` and `catalog show` (plan 27 decision 42). The console's command line offers `check`, `orphans` and `context` over its two projects; the index commands come with the serving layer's `knowledge` module.
