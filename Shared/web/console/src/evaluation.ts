import { request } from '@dbdatakit/shell'
import type { Diagnostic } from './agents'

// Plan 27a AG23 and AG24: the evaluation set of an agent on the Agents page. The server runs the cases and decides what it can; these are the shapes and the words, and what a person sees must tell a measurement of the suite from a judgement of the judge model.

export interface EvalCase { name: string; file: string; task: string; checks: string[]; hasRubric: boolean; passAt: number }
export interface EvalRunSummary {
  id: string; agent: string; agentVersion: string; channel: string; model: string | null; knowledgeVersions: string[] | null; trigger: string; score: number; passed: number; total: number; cost: number; startedAt: string; finishedAt: string
}
export interface EvalState {
  agent: string; version: string; channel: string; cases: EvalCase[]; diagnostics: Diagnostic[]; judgeBound: boolean
  running: { startedAt: string; total: number; done: number; trigger: string } | null
  runs: EvalRunSummary[]; minimum: { minimum: number; setBy: string; setAt: string } | null; promotion: { version: string; refusal: string | null }
}
export interface EvalCheck { kind: string; detail: string; method: 'suite' | 'judge'; score: number | null; message: string; judgement: string | null }
export interface EvalCaseResult { name: string; task: string; score: number; passed: boolean; answer: string; checks: EvalCheck[]; inputTokens: number; outputTokens: number; cost: number; problem: string | null }
export interface EvalRunDetail { run: EvalRunSummary; cases: EvalCaseResult[] }

const json = (method: string, body?: unknown): RequestInit => ({ method, body: body === undefined ? undefined : JSON.stringify(body) })
const base = (agent: string) => `/api/agents/${encodeURIComponent(agent)}/evaluation`

export const evaluationApi = {
  state: (agent: string) => request<EvalState>(base(agent)),
  run: (agent: string) => request<{ started: boolean }>(`${base(agent)}/run`, json('POST')),
  detail: (agent: string, id: string) => request<EvalRunDetail>(`${base(agent)}/runs/${encodeURIComponent(id)}`),
  setMinimum: (agent: string, minimum: number | null) => request<{ minimum: number | null; promotion: { version: string; refusal: string | null } }>(`${base(agent)}/minimum`, json('PUT', { minimum })),
}

/** A score as a whole percentage. */
export const scoreWords = (score: number): string => `${Math.round(score * 100)}%`

/** An amount of money in the small units model prices come in. */
export const moneyWords = (amount: number): string => (amount === 0 ? '$0' : amount < 0.01 ? `$${amount.toFixed(4)}` : `$${amount.toFixed(2)}`)

/**
 * What a check says, told apart by who decided it: the suite measured it ("held" or "did not hold"), the judge model judged it (its score and its reasons, said to be a judgement), or nobody could decide it (no judge model, a check the
 * host does not have): that is said too, and it is left out of the score.
 */
export function checkLine(c: EvalCheck): string {
  if (c.score === null) return `Not decided: ${c.message}`
  if (c.method === 'judge') return `Judged by the judge model at ${scoreWords(c.score)}: ${c.judgement ?? c.message} (a judgement, not a measurement)`
  return `${c.score >= 1 ? 'Held' : 'Did not hold'}: ${c.message}`
}

/** A run in a line: which version, which model, how many cases passed, the score and what it cost. */
export function runLine(r: Pick<EvalRunSummary, 'agentVersion' | 'model' | 'passed' | 'total' | 'score' | 'cost' | 'trigger'>): string {
  return `Version ${r.agentVersion}${r.model ? ` on ${r.model}` : ''}: ${r.passed} of ${r.total} case${r.total === 1 ? '' : 's'} passed, score ${scoreWords(r.score)}, cost ${moneyWords(r.cost)}${r.trigger === 'schedule' ? ' (scheduled)' : ''}.`
}

/** The scores over time, oldest first (the runs come newest first), for the line under the list. */
export const scoresOverTime = (runs: Pick<EvalRunSummary, 'score'>[]): string => [...runs].reverse().map((r) => scoreWords(r.score)).join(' → ')

/** What a minimum score does to a promotion, in words: nothing set, a promotion that would go on, or the refusal the server would give. */
export function promotionLine(state: Pick<EvalState, 'minimum' | 'promotion' | 'channel'>): string {
  if (!state.minimum) return `No minimum score is set for ${state.channel}, so a promotion is not held up by the evaluation.`
  return state.promotion.refusal ?? `Version ${state.promotion.version} reached the minimum of ${scoreWords(state.minimum.minimum)}: a promotion to ${state.channel} may go on.`
}

/** A minimum typed as a percentage: whole numbers from 0 to 100, blank for none; null when it is not one. */
export function parseMinimum(text: string): { value: number | null } | null {
  const t = text.trim().replace(/%$/, '')
  if (t === '') return { value: null }
  if (!/^\d{1,3}$/.test(t)) return null
  const n = Number(t)
  return n >= 0 && n <= 100 ? { value: n / 100 } : null
}
