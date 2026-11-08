import { ApiError } from '@dbdatakit/shell'
import type { CommandAnswer, PairedRunner, RunnerPublishData } from '../types'

/** What the person is doing: sending the project's files as a new version, or moving the version one channel serves to another channel. */
export type RunnerMode = 'publish' | 'promote'

export interface RunnerForm {
  mode: RunnerMode
  runner: string
  channel: string
  packageName: string
  /** One `slot=connection` per line. */
  bindText: string
  /** Make the version active at once instead of staging it. */
  serve: boolean
  /** Promotion only: the channel whose version is moved. */
  promoteFrom: string
  /** Leave the plans with this server's scheduler until they are cut over (the default) or let the runner schedule them from the first activation. */
  runnerSchedules: boolean
}

export const emptyRunnerForm = (project: string): RunnerForm => ({ mode: 'publish', runner: '', channel: '', packageName: project, bindText: '', serve: false, promoteFrom: '', runnerSchedules: false })

/** The `slot=connection` lines of the bindings box: the list to send, or the first line that is not one (a channel's connection slots are its own, so a half-written line must not be sent). */
export function parseBindings(text: string): { binds: string[]; problem: string | null } {
  const binds: string[] = []
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim()
    if (line === '') continue
    const eq = line.indexOf('=')
    if (eq <= 0 || eq === line.length - 1) return { binds: [], problem: `"${line}" is not slot=connection.` }
    binds.push(`${line.slice(0, eq).trim()}=${line.slice(eq + 1).trim()}`)
  }
  return { binds, problem: null }
}

/** Why the button cannot be pressed yet, in words; null when it can. The server decides again: this only explains what is missing or what the pairing is known not to allow. */
export function runnerPublishBlock(form: RunnerForm, runners: PairedRunner[] | undefined, allowed: boolean | undefined): string | null {
  if (allowed === false) return 'Publishing needs the author role on this server.'
  if (form.runner === '') return 'Choose the runner.'
  if (form.channel.trim() === '') return 'Name the channel.'
  if (form.packageName.trim() === '') return 'Name the package.'
  if (form.mode === 'promote' && form.promoteFrom.trim() === '') return 'Name the channel whose version is promoted.'
  if (form.mode === 'promote' && form.promoteFrom.trim() === form.channel.trim()) return 'The two channels are the same.'
  const problem = parseBindings(form.bindText).problem
  if (problem) return problem
  const roles = runners?.find((r) => r.name === form.runner)?.roles
  if (roles && !roles.includes(form.mode === 'promote' ? 'admin' : 'publisher')) {
    return form.mode === 'promote'
      ? `This server's pairing with ${form.runner} does not allow promoting (it needs an administrator pairing; it has ${roles.join(', ') || 'no role'}).`
      : `This server's pairing with ${form.runner} does not allow publishing (it needs the publisher role; it has ${roles.join(', ') || 'no role'}).`
  }
  return null
}

/** The inputs of the `project_publish` command for the form. */
export function runnerPublishRequest(form: RunnerForm) {
  return {
    server: form.runner,
    channel: form.channel.trim(),
    packageName: form.packageName.trim(),
    serve: form.mode === 'publish' && form.serve,
    bind: parseBindings(form.bindText).binds,
    ...(form.mode === 'promote' ? { promoteFrom: form.promoteFrom.trim() } : {}),
    runnerSchedules: form.mode === 'publish' && form.runnerSchedules,
  }
}

/** What the person reads when the publish came back refused: the command's own words (its errors, the diagnostics it found), or the transport's. */
export function runnerPublishFailure(error: unknown, answer: CommandAnswer<RunnerPublishData> | undefined): string | null {
  if (error instanceof ApiError) {
    if (error.status === 403) return /role/i.test(error.message) ? error.message : 'Publishing needs the author role on this server.'
    return error.message
  }
  if (error != null) return 'The server did not answer. Try again.'
  if (answer && answer.exit !== 0) {
    const doc = answer.document
    const lines = [...(doc?.errors ?? []), ...(doc?.diagnostics ?? []).filter((d) => d.severity === 'error').map((d) => `${d.location.file}: ${d.title}${d.fix ? ` ${d.fix}` : ''}`)]
    if (lines.length > 0) return lines.join(' ')
    if (answer.text) return answer.text
    return `The command stopped with exit code ${answer.exit} and nothing more to say.`
  }
  return null
}

/** One line of what a published version holds, for the staged-version card. */
export function taskLine(t: RunnerPublishData['tasks'][number]): string {
  const when = t.schedule ? `${t.schedule}${t.time_zone ? ` (${t.time_zone})` : ''}` : 'no schedule'
  const slots = t.slots.length > 0 ? `connections: ${t.slots.join(', ')}` : 'no connection slots'
  return `${when}; ${slots}`
}

/** What the staged-version card says about the state of the version: staged versions wait for every runner of the group to have them ready. */
export function modeText(data: RunnerPublishData): string {
  if (data.promoted_from) return `Version ${data.version} of ${data.package} is served on ${data.channel} (promoted from ${data.promoted_from}).`
  const base = data.mode === 'stage'
    ? `Version ${data.version} of ${data.package} is staged on ${data.channel}: the runners prepare it and it activates once every runner of the group has it ready (the Runner screens show it).`
    : `Version ${data.version} of ${data.package} is served on ${data.channel}.`
  return data.changed ? base : `${base} It is the same content as before; nothing changed.`
}
