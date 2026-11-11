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
