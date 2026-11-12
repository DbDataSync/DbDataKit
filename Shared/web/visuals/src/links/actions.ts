import type { ViewKind } from '../views/document'
import type { LinkParams, LinkRow } from '../views/links'
import type { ViewsClient } from '../client/types'

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

/** The staged changes sent as one: each line is what the server derives a change from (the link, the row, the parameters, the answers), and one key makes the whole submit one call (plan 32 DBR53b). */
export interface BatchRequest {
  lines: Pick<ActionRequest, 'link' | 'widget' | 'row' | 'parameters' | 'answers'>[]
  idempotencyKey: string
}

/** What one line of a set came to: its index in the request, and the Store's reason (a broken rule, a moved version) when it is the one that stopped the set. */
export interface BatchProblem { line: number | null; rule?: string; attribute?: string; message: string }

/**
 * What a submit of the set answers: **applied** (all of it, in one transaction), **waiting** (one change request of `count` changes, applied whole when it is approved), or nothing applied (`refused`: every broken rule against
 * its line; `conflict`: the rows whose version moved; `denied`; `failed`). After anything but `applied` or `waiting` the set is kept.
 */
export type BatchOutcome =
  | { status: 'applied'; count: number; message?: string }
  | { status: 'waiting'; count: number; requestId?: string; message?: string }
  | { status: 'refused' | 'conflict' | 'denied' | 'failed'; message: string; problems: BatchProblem[] }

/** What a host that runs actions gives the shell. */
export interface ActionHost {
  /** Submits the pending set as one batch (one transaction, one change request when the model asks for approval). Absent: a host that cannot says so and the shell does not stage. */
  submit?(request: BatchRequest): Promise<BatchOutcome>
  /** What will happen to a set over this model: applies together, or waits for approval as one request. */
  forecastBatch?(request: BatchRequest, signal?: AbortSignal): Promise<ActionForecast | null>
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

/** The host of a views client that runs actions (the module's, or a fixture's recorded ones): `runAction` and `forecastAction` of the client for the document being shown. Undefined when the client runs none. */
export function clientActionHost(client: Pick<ViewsClient, 'runAction' | 'forecastAction' | 'runBatch' | 'forecastBatch'>, ref: { kind: ViewKind; name: string }): ActionHost | undefined {
  const run = client.runAction
  if (!run && !client.runBatch) return undefined
  const forecast = client.forecastAction
  const submit = client.runBatch
  const batchForecast = client.forecastBatch
  return {
    run: run ? (request) => run.call(client, ref, request) : async () => ({ status: 'failed' as const, message: 'This source runs no single actions.' }),
    ...(forecast ? { forecast: (link, signal) => forecast.call(client, ref, link, signal) } : {}),
    ...(submit ? { submit: (request: BatchRequest) => submit.call(client, ref, request) } : {}),
    ...(batchForecast ? { forecastBatch: (request: BatchRequest, signal?: AbortSignal) => batchForecast.call(client, ref, request, signal) } : {}),
  }
}

/** The sentence under the title of the review: what will happen to the set, before the person submits it. */
export function batchForecastText(count: number, forecast: ActionForecast | null | undefined): string {
  if (forecast?.will === 'wait') return forecast.note ?? `This will wait for approval as one request of ${count} change${count === 1 ? '' : 's'}: nothing changes until it is approved, and then all of it applies together.`
  return forecast?.note ?? `All ${count} change${count === 1 ? '' : 's'} apply together, in one transaction: if one breaks a rule of the model, none is made.`
}

/** The sentence after a submit. */
export function batchOutcomeText(outcome: BatchOutcome): string {
  switch (outcome.status) {
    case 'applied': return outcome.message ?? `${outcome.count} change${outcome.count === 1 ? ' was' : 's were'} made together.`
    case 'waiting': return outcome.message ?? `Waiting for approval: one request of ${outcome.count} change${outcome.count === 1 ? '' : 's'} is kept, and nothing has changed yet.`
    default: return outcome.message
  }
}
