import type { LinkParams, LinkRow } from '../views/links'

/**
 * What the shell needs from the host that serves the app to run an action (plan 32 DBR53): the confirmed request goes to the host, which sends it to the server as the viewer. The server derives the change or
 * the task from the **published document**, never from what the page sends, so the request names the link and carries only the row the person clicked, the parameters and what they typed.
 */
export interface ActionRequest {
  /** The link of the document. */
  link: string
  /** The widget the click came from (the audit names it). */
  widget: string
  row: LinkRow
  parameters: LinkParams
  /** What the person typed or picked for each `ask` field. */
  answers: Record<string, string | undefined>
  /** Made when the popup opened: a double click, or a retry of a call whose answer was lost, is one change. */
  idempotencyKey: string
}

/** What will happen to the change, as the server says before the person confirms: it applies at once, it waits for approval, or a task runs. */
export interface ActionForecast { will: 'apply' | 'wait' | 'run'; note?: string }

/** One broken rule of the Store, with the field it is about. */
export interface ActionViolation { rule?: string; attribute?: string; message: string }

export type ActionOutcome =
  | { status: 'applied'; message?: string }
  /** The Store answered 202: the change is kept as a change request. */
  | { status: 'waiting'; requestId?: string; message?: string }
  | { status: 'started'; runId?: string; message?: string }
  /** The row changed since it was read (the version moved). */
  | { status: 'conflict'; message: string }
  /** The Store's rules refused it: every broken rule. */
  | { status: 'refused'; message: string; violations?: ActionViolation[] }
  /** The viewer may not (a `task` without the runner role, a model the app may not write). */
  | { status: 'denied'; message: string }
  | { status: 'failed'; message: string }

/** What a host that runs actions gives the shell. */
export interface ActionHost {
  run(request: ActionRequest): Promise<ActionOutcome>
  /** What will happen, before the person confirms; null when the host cannot say. */
  forecast?(link: string, signal?: AbortSignal): Promise<ActionForecast | null>
}

/** A key for one opening of the popup. */
export function newIdempotencyKey(): string {
  const c = (globalThis as { crypto?: { randomUUID?: () => string } }).crypto
  return c?.randomUUID ? c.randomUUID() : `k-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 12)}`
}

/** The sentence under the title that says what will happen, before the person confirms. */
export function forecastText(kind: 'change' | 'task', forecast: ActionForecast | null | undefined): string {
  if (kind === 'task') return forecast?.note ?? 'The task runs now, under your role on the runner.'
  if (!forecast) return 'The model decides whether this applies at once or waits for approval.'
  if (forecast.will === 'wait') return forecast.note ?? 'This will wait for approval: it is kept as a change request and applied when it is approved.'
  return forecast.note ?? 'This applies at once, through the model\'s rules, and is kept in its history.'
}

/** The sentence after the call, in the person's words. */
export function outcomeText(o: ActionOutcome): string {
  switch (o.status) {
    case 'applied': return o.message ?? 'The change was made.'
    case 'waiting': return o.message ?? 'Waiting for approval: the change is kept as a request and nothing has changed yet.'
    case 'started': return o.message ?? 'The task was started.'
    default: return o.message
  }
}
