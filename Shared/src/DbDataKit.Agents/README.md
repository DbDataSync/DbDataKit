# DbDataKit.Agents

The agent runtime of the suite (plan 27, plan 27a). It is a library: no host, no provider, no screen. A host (the console for back-end agents, the serving layer for front-end ones, a task for unattended ones) gives it a model, tools and a person; it runs the loop and keeps the rules.

## The loop (`AgentRunner`)

On `Microsoft.Extensions.AI` (`IChatClient`). The model is asked; each tool call it makes is checked, passed through the gate and the limits, run, and its result is given back; until the model answers with no more calls. A bad call is never an exception: a malformed call, an unknown tool and arguments that break the tool's schema each come back to the model as a result in words it can act on. A tool that throws is a result too.

- **Tools** (`AgentTool`, `CommandTools.From`): a router's commands as tools, with the names, descriptions and argument schema `ui mcp` offers (the same `McpServer.Offered` rule: changing commands are offered only when the host says so, `RefuseData` leaves out the verbs that read data), each carrying its **traits**.
- **Results are data** (`ToolResults.AsData`): wrapped with the tool and where the result came from, marked `trust="data"`, closed with a sentence that says it is not an instruction. The closing tag cannot occur inside, so a result cannot step out. A skill's body is the exception: it is the agent's own instructions and is wrapped as `<skill>` (`ToolResultKind.Skill`).
- **The scripted fake** (`DbDataKit.Agents.Testing.ScriptedChatClient`, `ScriptStep.Say/Call/Malformed/Refuse/WithUsage`): answers from a list, remembers every request, streams through the same path, fails loudly when the script runs out. No network anywhere in the tests.

## Prompting per action (`ApprovalGate`, `PromptingResolver`)

Three settings (ask, ask once per conversation, do not ask) and three layers: the tool's own default (the one its author declared, else the one its traits give: reads do not ask, writes of definitions ask once, the rest ask), the install policy (a minimum per trait and a setting for a tool by name) and the agent definition's setting. **The strictest applies.** The person sees `ApprovalPrompt.Preview` (the command, its arguments and its traits); only the approver's answer counts, so nothing in a model message or a tool result can stand for an approval. Every ask, approval and denial is an event naming the person, the agent and its version. An unattended run (no approver) runs only *do not ask*; the rest go to an `IProposalSink`.

## Limits and cost (`RunLimits`)

Per conversation, person and install on turns, tool calls, tokens and cost (`ModelPrice.CostOf`: the provider's reported usage times the price). A limit that is reached stops the run with a sentence that says which; administrators are told once when a scope passes the threshold (`ILimitNotifier`); `Commit` adds a finished run to the `IUsageLedger`.

## Definitions

- `agent/1` (`AgentDefinitionLoader`): name, audience, model slot, instructions, skills, tools (`command:` or `action:` with `prompting:`), scopes, knowledge, memory, subagents, limits, output. Strict, with file and line; the JSON Schema is `schemas/agent.schema.json`.
- `agent-policy/1` (`AgentPolicyLoader`): prompting minimums per trait and per tool, limits per scope, `notify-at`, the data rule for cloud providers. Policy *text* is a guardrail item of the install knowledge project (`DbDataKit.Knowledge`), not part of this.
- `agent check` (`AgentChecker`): every tool, skill and subagent exists, every scope resolves, no tool asks for less than the policy's minimum; each problem at the line where it is written, with "did you mean".

## Context (`ContextAssembler`, `TurnContext`, `BoundKnowledgeProvider`)

Always the same order: the platform's rules (`PlatformRules`, versioned with the code), the install policy, the agent's instructions, the instructions that apply where the work is (from `DbDataKit.Knowledge`'s `ScopeMatcher`, broad to narrow), the guardrails in a section of their own, memories, references, the skills on offer. The record of a turn names every part with its version (`ContextPartRecord`), so a conversation replays to the same text. `InPlayTracker` works out what is in play from the tool calls, never from a guess.

## The model's space (`ContextBudget`, `ContextFitter`) and skills (`SkillCatalog`)

What is always given is counted (`ITokenCounter`; with no local tokenizer `EstimateCounter`: characters divided by 3.5 plus 15%, and the refusal says so) and compared with a share of the window; over it, a publish is refused naming what is largest. At run time references give way first, then old turns (summarised, recorded), never an instruction. A skill is offered by name and description; `skill_load` returns the body and records its version; the largest body must fit in the room left.

## What a host shows people (`Insight`, `PolicyEditing`) and runs (`Session`)

The console's screens (plan 27a AG15 to AG17, AG29 to AG32) compute nothing of their own; they show what these functions say, by the rules the run uses.

- **What each tool will ask** (`ToolPromptingReport`): per tool the setting that applies (`PromptingResolver.Resolve`, the same call the gate makes), the layer that sets it (the install policy's setting for the tool, the policy's minimum for a trait, the agent, the tool's own default or its traits) and a warning when the agent's own setting is below what a higher layer needs, so it has no effect.
- **The model's space** (`ModelSpace`): the always-given parts (instructions, tool definitions, skill descriptions, the references an agent always has, the bound instructions and guardrails) by kind and by part against the budget, how they were counted, and, when over, the refusal that names what is largest.
- **Skills** (`SkillViews`): each skill's description and version, the size of its body against the room left, the refusal that names a skill that does not fit, and the files that export it.
- **The install policy as a file** (`PolicyEditing.Write`): an `agent-policy/1` text the loader reads back equal; `Describe(before, after)` lists what a change does in sentences and marks every one that makes the install less strict, which a host asks the person to confirm and records.
- **The approval desk** (`ApprovalDesk`): the steps that wait for a person. `ApproverFor(conversation)` is the conversation's `IApprover`; `Answer` is the person's own act and counts only for the person a step is for; a step nobody answers within the timeout expires, which is a no. Nothing a model says reaches it.
- **A session** (`AgentSession`): one conversation of an agent with a person. A message runs the loop with the gate, the limits (the agent's own narrow the install's) and the context of each turn, and writes to an `IConversationLog` the words, one record per model turn (tokens, cost, what the turn was given) and every event naming the person, the agent and its version. A run that ends on a limit, a denial or a refusal says so in the conversation's status; the next message goes on.

## Memories (`MemoryService`)

What an agent remembers, in two kinds: about a **person** (only that person lists, edits and deletes it; another person's id is simply not found) and about the **work** (a workspace or work item: whoever opens it). The agent writes with `memory_remember` (asks once per conversation) and forgets with `memory_forget` (asks every time); an install or an agent can only make these stricter. A value that looks like a secret is refused in words and nothing is written; a memory is at most 500 characters. Memories reach the model through the context assembly after the instructions and before references, newest first inside a room the host gives them, each recorded in the turn and re-read every turn so a deleted one is not given again. Each kind is turned off per agent (`memory:` in `agent.yml`) or for the install (`memory: { person: off }` in `agent-policy.yml`); off means neither written nor read. Shared learning is never memory: it is a suggestion to the knowledge owner. The store is `IMemoryStore` (the `agents` module's `memory` table in a host).

## Subagents (`SubagentTools`)

Each name in `subagents:` is a tool `agent_<name>` taking a `task` and the `context` the parent passes. It is the same person; its reach is what the parent can call, narrowed by the tools its own file names (it can never call a tool its parent could not); it has its own instructions and sees only the task and context; its steps ask through the parent's gate and desk; its turns, tool calls and tokens count to the parent (`ToolOutcome.Spent`); it nests two levels at most (a third is refused in words); its events carry its own name, its parent and its depth, so the transcript nests.

## Views in an answer (`AnswerViewTool`)

`show_view` is how an agent returns a table, a matrix or a chart: a dashboard widget and its data inline. The host passes a check (`DbDataKit.Views`'s `AgentAnswerViews.Check`: the dashboards' own widget reader and closed Vega-Lite subset, the kinds of the agent's `output:`, every encoded field a column, no address anywhere). A refusal says what is wrong so the model can correct the call. A link in the answer's text is only text; nothing is fetched.

## Publishing (`AgentDefinitions`)

The kinds `agent`, `skill` and `agent-policy` for plan 30's registry: validators, the agent's model slot as a slot of the version (`model:<slot>`, so a promotion with it unbound in the channel is refused naming it), `skill:` and `agent:` dependencies, formats, target types. One version per publish, the instruction files by hash, the same content again changes nothing.

## When a model is down (`FallbackChatClient`, `AgentPolicy.CloudFallback`)

A slot's list is retried with back-off, then falls through to the next model the install policy allows; `data-rules: cloud-providers: fallback: refuse` (a tenant-only install) keeps a cloud provider out of every position after the first.

## Evaluation sets (`Evaluation/`, plan 27a AG23)

An agent's cases are the files `tests/evaluation/*.yml` beside its definition, in the format `evaluation/1` (`EvaluationCaseLoader`, strict, with file and line; `schemas/evaluation.schema.json`): a `task`, `fixtures` that go with it, `pass-at` (the score at which the case passes, 1 by default) and `expect`, a list of one-key checks: `tools-called`, `tools-not-called`, `answer-contains`, `answer-matches` (a regular expression), `rows-match-verified-query: <id>`, `check: <name>` (a check the host knows, such as that the workspace compiles) and `rubric` (free text). `EvaluationRunner.RunAsync` puts each case to the agent through an `IEvaluationHost` (`AgentEvaluationHost` is the one over `AgentRunner`: it records each tool call with what it returned, and the usage) and checks it. **The suite decides what it can**; **a judge model reads only a rubric**, and its score and reasons are kept as a judgement apart from the measurements (`CheckResult.Method` is `suite` or `judge`); with no judge the rubric is not judged, left out of the score and counted in the run. `RowSets` compares the rows of the agent's query (read from the result of its tool call) with a verified query's by column name, in any order, numbers by value. A case that cannot be run scores 0 with the reason and the set goes on. `EvaluationGate.Refusal` is what a promotion below the minimum score (a recorded choice per agent and channel, `EvaluationStore` in `DbDataKit.State`) is told, naming the score. A run is a record (agent version, knowledge versions given, model, score per case, cost), never a gate of a pull request.
