# AGENTS: agents as something people author, test and run

## Goal

An agent is authored and checked like any other project: its definition (`agent/1`), skills and policy, the model provider it uses, scripted test
conversations in its editor, and evaluation runs before it is promoted. It runs in the console's chat, in the Gateway for end users, and unattended
inside an integration. What an agent can do once it runs (the operations it calls, the MCP endpoints, its data rules) is AGENTTOOLS'.

## What exists today

- The agent runtime and providers (`DbDataKit.Agents`), `agent/1`, `skill`, `agent-policy/1`, limits and cost, context assembly, the console's chat
  over agents whose tools only read (`docs/agents/`).

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| AGENTS-1 | The agent project editor's scripted test conversations, the knowledge file preview of the subjects and agents it reaches, on the recipe | AUTHORING-32 | M | Playwright: run a scripted conversation of an agent project; a knowledge file's preview lists the agents an instruction reaches |
| AGENTS-2 | The chat over a real model, providers and usage per channel, fallback lists, the console agent that offers the view-drafting tools | none | M | a recorded provider conversation in the console's chat shows usage and falls back to the second connection when the first fails |
| AGENTS-3 | Evaluation runs as a Runner task: an agent's evaluation set runs on a Runner with the agent's model slot bound there; the promotion check (`EvaluationGate`) asks for the score | none | M | a Runner task runs a set and records scores; a promotion below the minimum is refused naming the score |
| AGENTS-4 | Front-end agents in the Gateway's `mcp` module acting as the signed-in person, with the operations and agent data rules of AGENTTOOLS-10 as their tools; an app's assistant panel; a published agent as one MCP tool | KNOWLEDGE-2, AGENTTOOLS-10 | L | an MCP client calls a published agent through the module as a person and gets only what that person may read; Playwright of the assistant panel |
| AGENTS-5 | Unattended agents in integrations: the agent runtime as a library in a task's process, the agent definition a dependency of the package, its model connection a slot | none | M | a Runner task runs an agent step with a recorded provider and records the turn |

## Shared areas touched

- `DbDataKit.Agents`, the agent project kind and its editor, the console's chat, the Gateway's `mcp` module, `docs/agents/`.
