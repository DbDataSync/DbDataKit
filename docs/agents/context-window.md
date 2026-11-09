# The model's space and the instruction budget

> **Design, not built yet** (sliced in [plan 27a](../plans/27a-knowledge-slices.md); what exists today is in [agent-tools.md](../agent-tools.md)). This page explains the design in [plan 27](../plans/27-knowledge-agents-integrations-work.md), section
> 3.5b. When agents are built, this page becomes part of their help.

## The context window

A model can take only so much text in one request. That limit is its **context window**, and it is counted in **tokens**: pieces of words.
In English a token is about four characters, so 1,000 tokens is roughly 750 words.

The window depends on the model:

| Model | Window, roughly |
| --- | --- |
| small models run locally | a few thousand tokens |
| common hosted models | 128,000 to 200,000 tokens |
| some larger models | more |

Everything in a request shares the window:

- the platform's rules and the install policy;
- the agent's own instructions;
- the **tool definitions**: each tool's name, description and the schema of its arguments;
- instructions and guardrails given because their scope applies ([what an agent knows](context.md));
- references found by search;
- the conversation so far, and the results of the tools the agent has called;
- room left for the model's answer.

## Why the size matters

For scale, Build's agent kit (`DbDataBuild/src/DbDataBuild.Cli/AgentKit/SKILL.md`) is about 6,200 words (40 KB), roughly 10,000 tokens,
before any tool definitions are added. A small local model could not take it at all. A large hosted model takes it easily, but:

- what instructions use is not left for the conversation and the tool results;
- every request costs more, since providers charge per token;
- with a very long context, models tend to pay less attention to what is in the middle of it.

## The budget for what is always given

So each agent has a **budget** for what it is always given: a share of its model's window (for example 20%). That covers the agent's own
instructions, its tool definitions, and every instruction and guardrail bound to anything in its scopes.

**Instructions are never cut silently** to make them fit:

- **When the agent is published or promoted**, everything always given is counted, with the bound model's own way of counting tokens. Over
  the budget, the publish is refused, naming the largest items, so someone decides what to shorten.
- **When the owner of a subject approves a new instruction or guardrail**, the agents it reaches are checked the same way, and the approval
  says which agent it would push over its budget.
- **While the agent runs**, only references found by search and the oldest part of the conversation give way when room runs short. An
  instruction is never dropped.

Because the window belongs to the model, binding an agent to a different model (for example a small local model in a test channel) can put
it over budget. That is found when it is published to that channel, not in the middle of a conversation.
