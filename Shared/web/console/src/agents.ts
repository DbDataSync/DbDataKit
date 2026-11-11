import { request } from '@dbdatakit/shell'

/** A problem found in a file, with where it is (plan 27a: `agent check`, the strict readers). */
export interface Diagnostic { code: string; severity: string; file: string; line: number; column: number; message: string; fix: string | null }

/** The three prompting settings, as the server writes them. */
export type Prompting = 'Ask' | 'AskOnce' | 'DoNotAsk'

export interface AgentListing { name: string; title: string | null; description: string | null; audience: string; modelSlot: string; problems: number; modelBound: boolean }

/** What one tool will ask, and the layer that sets it (AG30). `warning` is set when the agent's own setting has no effect. */
export interface ToolPrompting {
  tool: string; source: string; traits: string[]; own: Prompting; ownFrom: string; policyForTool: Prompting | null; policyMinimum: Prompting | null; policyMinimumTrait: string | null
  agent: Prompting | null; effective: Prompting; layer: string; warning: string | null
}

export interface SpaceGroup { name: string; tokens: number; share: number }
export interface SpacePart { name: string; tokens: number }

/** The model's space for an agent: what is always given against the budget (AG31). */
export interface ModelSpace {
  agent: string; window: number; budgetTokens: number; totalTokens: number; over: boolean; usedShareOfBudget: number; basis: string; groups: SpaceGroup[]; parts: SpacePart[]; refusal: string | null
}

export interface SkillView { name: string; description: string; version: string; file: string; bodyTokens: number; descriptionTokens: number; fits: boolean; refusal: string | null; exportFiles: Record<string, string> }

export interface AgentDetail {
  name: string; file: string; yaml: string; diagnostics: Diagnostic[]; prompting: ToolPrompting[]; space: ModelSpace | null; skills: SkillView[]; instructions: string[]; tools: string[]; modelBound: boolean
}

export interface PolicyChange { setting: string; loosens: boolean; text: string }
export interface PolicyPreview { diagnostics: Diagnostic[]; changes: PolicyChange[]; loosens: boolean }

export interface Conversation {
  id: string; agent: string; agentVersion: string; person: string; personName: string; origin: string; channel: string | null; workspace: string | null; title: string | null; status: string; stopReason: string | null; startedAt: string; updatedAt: string
}
export interface TurnText { conversationId: string; seq: number; role: string; content: string; createdAt: string }
export interface TurnRecord { id: string; conversationId: string; turn: number; person: string; agent: string; agentVersion: string; model: string | null; inputTokens: number; outputTokens: number; cost: number; contextPrint: string | null; contextRecord: string | null; recordedAt: string }
export interface AuditRow { id: string; conversationId: string | null; turn: number | null; kind: string; person: string; personName?: string; agent: string; agentVersion: string; origin: string; tool: string | null; arguments: string | null; detail: string | null; occurredAt: string }
export interface Approval { id: string; conversationId: string | null; person: string; personName?: string; answeredByName?: string; agent: string; agentVersion: string; origin: string; tool: string; preview: string; arguments: string | null; traits: string; status: string; requestedAt: string; answeredAt: string | null; answeredBy: string | null; note: string | null }
export interface ConversationView { conversation: Conversation; text: TurnText[]; turns: TurnRecord[]; audit: AuditRow[]; approvals: Approval[]; running: boolean }
export interface UsageRow { key: string; personName?: string; turns: number; inputTokens: number; outputTokens: number; cost: number }
export interface AgentToken { id: string; name: string; createdAt: string; expiresAt: string | null; revokedAt: string | null; lastUsedAt: string | null; active: boolean }
export interface MadeToken { token: string; user: string; record: { id: string; name: string; createdAt: string; expiresAt: string | null } }
export interface ModelEntryView { id: string; window: number; maxOutput: number; priceInput: number | null; priceOutput: number | null }
export interface ProviderView {
  name: string; file: string; yaml: string; title: string | null; kind: string | null; endpoint: string | null; cloud: boolean | null; purpose: string | null; keyReference: string | null
  keyAvailable: boolean; keyProblem: string | null; models: ModelEntryView[]; diagnostics: Diagnostic[]
}
export interface SlotChoiceView { provider: string; model: string; usable: boolean; problem: string | null }
export interface SlotBindingView { slot: string; channel: string; choices: SlotChoiceView[] }
export interface ModelsView {
  providers: ProviderView[]; slotsYaml: string; slotDiagnostics: Diagnostic[]; bindings: SlotBindingView[]; agentSlots: { slot: string; agents: string[]; bound: boolean }[]; secretPrefix: string
}
export interface ProviderTest { ok: boolean; message: string; answer: string | null; inputTokens: number | null; outputTokens: number | null; milliseconds: number; temporary: boolean }

export interface Usage { byPerson: UsageRow[]; byAgent: UsageRow[]; install: UsageRow | null }

const json = (method: string, body?: unknown): RequestInit => ({ method, body: body === undefined ? undefined : JSON.stringify(body) })

export const agentsApi = {
  definitions: () => request<AgentListing[]>('/api/agents/definitions'),
  definition: (name: string) => request<AgentDetail>(`/api/agents/definitions/${encodeURIComponent(name)}`),
  check: (name: string, yaml: string) => request<AgentDetail>(`/api/agents/definitions/${encodeURIComponent(name)}/check`, json('POST', { yaml })),
  save: (name: string, yaml: string) => request<AgentDetail>(`/api/agents/definitions/${encodeURIComponent(name)}`, json('PUT', { yaml })),
  policy: () => request<{ yaml: string; diagnostics: Diagnostic[] }>('/api/agents/policy'),
  previewPolicy: (yaml: string) => request<PolicyPreview>('/api/agents/policy/preview', json('POST', { yaml })),
  savePolicy: (yaml: string, confirmed: boolean) => request<{ saved: boolean; changes: PolicyChange[] }>('/api/agents/policy', json('PUT', { yaml, confirmed })),
  conversations: (all: boolean) => request<Conversation[]>(`/api/agents/conversations?all=${all}`),
  start: (agent: string, title?: string) => request<Conversation>('/api/agents/conversations', json('POST', { agent, title })),
  conversation: (id: string) => request<ConversationView>(`/api/agents/conversations/${encodeURIComponent(id)}`),
  send: (id: string, text: string) => request<void>(`/api/agents/conversations/${encodeURIComponent(id)}/messages`, json('POST', { text })),
  cancel: (id: string) => request<void>(`/api/agents/conversations/${encodeURIComponent(id)}/cancel`, json('POST')),
  approvals: (pending: boolean, all: boolean) => request<Approval[]>(`/api/agents/approvals?pending=${pending}&all=${all}`),
  answer: (id: string, approve: boolean, note?: string) => request<Approval>(`/api/agents/approvals/${id}`, json('POST', { approve, note })),
  audit: (filter: { person?: string; agent?: string; origin?: string; kind?: string }) => {
    const query = new URLSearchParams(Object.entries(filter).filter(([, v]) => v) as [string, string][]).toString()
    return request<AuditRow[]>(`/api/agents/audit${query ? `?${query}` : ''}`)
  },
  usage: () => request<Usage>('/api/agents/usage'),
  models: () => request<ModelsView>('/api/agents/models'),
  checkProvider: (name: string, yaml: string) => request<{ diagnostics: Diagnostic[] }>(`/api/agents/models/providers/${encodeURIComponent(name)}/check`, json('POST', { yaml })),
  saveProvider: (name: string, yaml: string) => request<{ saved: boolean; diagnostics: Diagnostic[] }>(`/api/agents/models/providers/${encodeURIComponent(name)}`, json('PUT', { yaml })),
  deleteProvider: (name: string) => request<void>(`/api/agents/models/providers/${encodeURIComponent(name)}`, json('DELETE')),
  setKey: (name: string, key: string) => request<{ stored: boolean; reference: string }>(`/api/agents/models/providers/${encodeURIComponent(name)}/key`, json('PUT', { key })),
  testProvider: (name: string, model: string) => request<ProviderTest>(`/api/agents/models/providers/${encodeURIComponent(name)}/test`, json('POST', { model })),
  checkSlots: (yaml: string) => request<{ diagnostics: Diagnostic[] }>('/api/agents/models/slots/check', json('POST', { yaml })),
  saveSlots: (yaml: string) => request<{ saved: boolean; diagnostics: Diagnostic[] }>('/api/agents/models/slots', json('PUT', { yaml })),
  tokens: () => request<AgentToken[]>('/api/agents/tokens'),
  makeToken: (name: string, days?: number) => request<MadeToken>('/api/agents/tokens', json('POST', { name, days })),
  revokeToken: (id: string) => request<void>(`/api/agents/tokens/${encodeURIComponent(id)}`, json('DELETE')),
}

/** The setting as the files write it. */
export const promptingName = (p: Prompting): string => ({ Ask: 'ask', AskOnce: 'ask-once', DoNotAsk: 'do-not-ask' })[p]

/** What a setting means, in a sentence a person reads beside it. */
export const promptingMeaning = (p: Prompting): string =>
  ({ Ask: 'The person is asked every time before it runs.', AskOnce: 'The person is asked the first time in a conversation; after that it runs.', DoNotAsk: 'It runs without asking.' })[p]

/** A share as a percentage with no decimals, never above what it is (a share over 100% says so). */
export const percent = (share: number): string => `${Math.round(share * 100)}%`

/** The words for what happened to a step or a run, for the record. */
export function eventWord(kind: string): string {
  const words: Record<string, string> = {
    'approval-asked': 'asked', 'approval-approved': 'approved', 'approval-denied': 'denied', 'tool-called': 'ran', 'tool-refused': 'refused', 'tool-failed': 'failed',
    'limit-reached': 'stopped at a limit', stopped: 'stopped', 'context-given': 'was given context', 'skill-loaded': 'loaded a skill', 'policy-loosened': 'loosened the policy', 'policy-changed': 'changed the policy',
    'agent-saved': 'saved the definition', 'session-started': 'started a session', 'model-answered': 'model answered',
  }
  return words[kind] ?? kind
}

/** Groups the record by conversation (or, for an outside agent's steps with none, by person and agent), newest first: one entry per session. */
export function sessions(rows: AuditRow[]): { key: string; person: string; agent: string; agentVersion: string; origin: string; last: string; rows: AuditRow[] }[] {
  const groups = new Map<string, AuditRow[]>()
  for (const row of rows) {
    const key = row.conversationId ?? `${row.origin}:${row.person}:${row.agent}@${row.agentVersion}`
    groups.set(key, [...(groups.get(key) ?? []), row])
  }
  return [...groups.entries()]
    .map(([key, list]) => {
      const ordered = [...list].sort((a, b) => a.occurredAt.localeCompare(b.occurredAt))
      const first = ordered[0]
      return { key, person: first.personName ?? first.person, agent: first.agent, agentVersion: first.agentVersion, origin: first.origin, last: ordered[ordered.length - 1].occurredAt, rows: ordered }
    })
    .sort((a, b) => b.last.localeCompare(a.last))
}

/** Whether a conversation is waiting on its person: a step of it is still pending. */
export const waitingOn = (approvals: Approval[]): Approval | null => approvals.find((a) => a.status === 'pending') ?? null

/** The status of a conversation in words: how its last run ended. */
export const statusWord = (status: string): string =>
  ({ open: 'Ready', completed: 'Finished', 'limit-reached': 'Stopped at a limit', denied: 'A step was not approved', refused: 'The model refused', failed: 'Failed' })[status] ?? status

/** Cost as a short amount; nothing recorded is a dash, not 0. */
export const cost = (value: number): string => (value === 0 ? '-' : value < 0.01 ? '<0.01' : value.toFixed(2))

/** What a person puts in the outside agent's MCP configuration to have its session recorded here and its steps asked in the inbox (`ui mcp --report-to`). The secret is shown once, when it is made; until then the value is a placeholder. */
export function connectCommand(product: string, consoleUrl: string, user: string, token: string | null): { command: string; environment: { name: string; value: string }[] } {
  return {
    command: `${product} ui mcp --ask --report-to ${consoleUrl.replace(/\/+$/, '')}`,
    environment: [{ name: 'DBDATAKIT_CONSOLE_USER', value: user }, { name: 'DBDATAKIT_CONSOLE_TOKEN', value: token ?? '<the token, shown when it is made>' }],
  }
}

/** A token's state in a word: active, revoked or expired. */
export const tokenState = (token: AgentToken): string => (token.revokedAt ? 'revoked' : token.active ? 'active' : 'expired')

/** A starting file for a provider of a kind, with the key reference this console keeps a typed key under. */
export function providerTemplate(kind: string, name: string): string {
  const key = `secret:dbdata:console:model-provider:${name}:key`
  const models: Record<string, string> = {
    anthropic: '  - { id: claude-sonnet-4-5, window: 200000, max-output: 8192, price: { input: 3, output: 15 } }',
    openai: '  - { id: gpt-4.1, window: 1000000, max-output: 16384, price: { input: 2, output: 8 } }',
    foundry: '  - { id: gpt-4.1, window: 1000000, max-output: 16384 }',
    ollama: '  - { id: "llama3.1:8b", window: 128000 }',
    'openai-compatible': '  - { id: my-model, window: 32000 }',
  }
  const head = `model-provider: 1\nname: ${name}\nkind: ${kind}\n`
  const endpoint = kind === 'foundry' ? 'endpoint: https://<resource>.openai.azure.com/openai/v1\n' : kind === 'openai-compatible' ? 'endpoint: http://localhost:8080/v1\nlocality: local\n' : ''
  const keyLine = kind === 'ollama' ? '' : `api-key-secret: ${key}\n`
  return `${head}${endpoint}${keyLine}models:\n${models[kind] ?? models['openai-compatible']}\n`
}

/** What a slot's binding says in a line: the primary and the fallback in order. */
export const bindingLine = (b: SlotBindingView): string => b.choices.map((c, i) => `${i === 0 ? '' : 'then '}${c.provider}/${c.model}${c.usable ? '' : ' (not usable)'}`).join(' ')
