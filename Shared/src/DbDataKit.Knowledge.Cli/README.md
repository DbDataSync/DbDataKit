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

## The module as a library (`KnowledgeProjectModule`, KN11)

What the serving layer's `knowledge` module does, without the host: it follows the kind `knowledge-project` as an `IDefinitionSubscriber`. **A version has an index of its own** (the channel `release/name@version` of the `knowledge` and `knowledge_vectors` modules), built whole before the channel moves to it, so **activation is the definitions' head moving**: a search reads the head's active version at each question and sees the whole of one version, and a rollback moves the head back to an index that is still there (the served, the previous and a staged version keep theirs, the others are pruned). `StageAsync` checks every item (the version is refused naming file and line when one does not check), writes the words, gives each chunk the vector the served version has for the same text (`KnowledgeVectorStore.CopyUnchanged`) and embeds only the rest; with no model, or one that is down, words answer and the report says why. `Reader` is the reading commands over every project served, under the caller's rules; `Health` says the version, counts, model and whether the vectors are complete. The kind `build-metadata` (`BuildMetadataDefinitions`, plan 27a KN13) is the metadata documents of a Build project on a connection; `BuildMetadata` is the subscriber that keeps one generated item per model and per column (`GeneratedBuildKnowledge`) in the release's channel `release/generated`.

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

## The noun `query` (`VerifiedQueryNoun`, KN21)

`query list-verified [--model] [--channel]` lists the verified queries the caller may see (the questions, who verified each and against which version of the model, and whether it still holds); `query run-verified <id> [--param name=value ...] [--max-rows N] [--channel]` runs one **as the caller**: the host's `IVerifiedQueryReader.RunAsync` runs the statement on the model with the caller's own session, so two people running the same query get what each may see. The pinned request is what runs (its `{"param": name}` markers take the values; an unknown or missing parameter is refused naming what the query takes); a saved query runs as `SELECT * FROM name(arg := value)`. A query marked `needs-reverification` is still found and listed and is not run as verified. The answer names the verified query, its version and who verified it (`verified_query` in the document), so the record of a turn (the tool call and its answer) says which one was used, and notes when the model is at a newer version than the query was verified against. `StateVerifiedQueryReader` is the reader over the index under `KnowledgeAccess`. The host that supplies it is the serving layer's `knowledge` module (plan 20, not built); `KnowledgeHost.TranslateSql` makes `knowledge check` compare a query's SQL with its request.

## Re-verification when a model changes (`KnowledgeProjectModule.SemanticModels`, KN22)

`SemanticModels` is the `IDefinitionSubscriber` of the kind `semantic-model`: told the version a channel now serves (or null), it checks every verified query about the model in every index of the release (`VerifiedRecheck.Problems`: the members exist, the columns keep their types against the catalog it saw last, a saved query is still there, the host's `RequestValidator` accepts it) and marks a failing one `needs-reverification` with the reason; `VerifiedNotifier` is told once per failure so the host can tell the owner. A query marked by an earlier check that holds again is restored. It never throws and never holds the model up; a catalog it cannot read checks nothing. A project version staged later keeps the mark of a query whose text did not change.
