// The test of a Sync draft's script, on a Runner (Design: docs/authoring-metadata.md#a-scripts-test-runs-on-a-runner): the Workspace server builds the draft, the console stages the build
// in the channel the person picks as the signed-in person (production as any other, with a warning), and runs the set's task `script-test` there; the answer comes back through the console and is kept nowhere. The
// Workspace server runs no script: it only builds.
import type { BuildDetail, BuildSummary, ScriptTest } from './api'
import type { RunAnswer } from './authoringReads'
import type { DeployAnswer, DeployBody, RunnerVersion } from './deploys'
import { buildBusy } from './model'

/** The set's task that compiles a script and runs it against generated input. */
export const SCRIPT_TEST_TASK = 'script-test'

/** The channel a test stages in when the draft was never deployed to a Runner. */
export const SCRIPT_TEST_CHANNEL = 'dev'

/** The production channel: a test is staged there as in any other, after a warning. */
export const PRODUCTION = 'prod'

/** The sentence shown when the person picks production: the test goes ahead after it, and the Runner's roles decide whether it may. */
export const scriptTestProductionWarning = (channel: string, runner: string) =>
  `This stages the draft in production (${channel} on ${runner}): it replaces the version staged there, not the one production serves. The script runs against generated input and opens no connection.`

/** Where a test runs: the Workspace server (the console's id), the draft, and the Runner (the console's id) and channel it is staged in. */
export interface ScriptTestPlace { workspaceServer: string; project: string; workspace: string; runner: string; channel: string }

/** What a test is doing now, for the words beside the button. */
export type ScriptTestPhase = 'building' | 'staging' | 'running'

/** A test that ended: what the script did, the run on the Runner (its link and the Runner's name), the build and the version it staged. */
export interface ScriptTestRun { test: ScriptTest; run_id: string; link: string; runner_name: string; channel: string; build: string; version: number | null }

/** The calls a test makes, so a test of this module can stand in for the servers. */
export interface ScriptTestCalls {
  requestBuild: (project: string, id: string) => Promise<BuildSummary>
  build: (project: string, id: string, build: string) => Promise<BuildDetail>
  deploy: (body: DeployBody) => Promise<DeployAnswer>
  answer: (deploy: string, task: string, inputs: Record<string, string>) => Promise<RunAnswer<ScriptTest>>
  followAnswer: (deploy: string, run: string) => Promise<RunAnswer<ScriptTest>>
  wait: (ms: number) => Promise<void>
}

/** How often a build and a run are asked again, and for how long at most. */
const POLL_MS = 1200
const BUILD_LIMIT_MS = 10 * 60_000
const RUN_LIMIT_MS = 6 * 60_000

/**
 * The Runner and channel a test is staged in, unless the person chose: the Runner and channel of the draft's newest deploy to a Runner, whatever the channel, and otherwise the first
 * Runner the console has, in `dev`. Null when the console has no Runner.
 */
export function defaultScriptTestPlace(newest: RunnerVersion | null, runners: readonly { id: string }[]): { runner: string; channel: string } | null {
  if (newest) return { runner: newest.target, channel: newest.channel }
  return runners[0] ? { runner: runners[0].id, channel: SCRIPT_TEST_CHANNEL } : null
}

/** Why a channel cannot take a test, or null when it can. */
export function channelProblem(channel: string): string | null {
  if (!/^[a-z0-9][a-z0-9_-]{0,63}$/.test(channel)) return 'A channel is lower-case letters, digits, - and _.'
  return null
}

/**
 * Tests the script `name` of the draft on a Runner: builds the draft, waits for the build, stages it in `place.channel` of `place.runner` through the console, runs `script-test` there and waits
 * for its answer. Throws with the reason when the build fails, the console or the Runner refuses, or the run fails.
 */
export async function testScriptOnRunner(calls: ScriptTestCalls, place: ScriptTestPlace, name: string, rows: number, onPhase: (phase: ScriptTestPhase) => void): Promise<ScriptTestRun> {
  const refused = channelProblem(place.channel)
  if (refused) throw new Error(refused)

  onPhase('building')
  let build = await calls.requestBuild(place.project, place.workspace)
  for (let waited = 0; buildBusy(build.status); waited += POLL_MS) {
    if (waited > BUILD_LIMIT_MS) throw new Error(`Build ${build.id} has not ended after ${BUILD_LIMIT_MS / 60_000} minutes; see the Builds tab.`)
    await calls.wait(POLL_MS)
    build = (await calls.build(place.project, place.workspace, build.id)).build
  }
  if (build.status !== 'succeeded') throw new Error(`The draft did not build, so the script was not tested: ${build.message ?? build.status}`)

  onPhase('staging')
  const staged = await calls.deploy({ workspaceServer: place.workspaceServer, project: place.project, workspace: place.workspace, build: build.id, target: place.runner, channel: place.channel, serve: false, note: `Test of the script ${name}` })

  onPhase('running')
  const deploy = staged.deploy.id
  let run = await calls.answer(deploy, SCRIPT_TEST_TASK, { name, rows: String(rows) })
  for (let waited = 0; run.state === 'running'; waited += POLL_MS) {
    if (waited > RUN_LIMIT_MS) throw new Error(`The test is still running on ${run.runner_name}; its run is ${run.link}.`)
    await calls.wait(POLL_MS)
    run = await calls.followAnswer(deploy, run.run_id)
  }
  if (run.state !== 'done' || !run.answer) throw new Error(run.error ?? `The test's run on ${run.runner_name} ended without an answer.`)
  return { test: run.answer, run_id: run.run_id, link: run.link, runner_name: run.runner_name, channel: run.channel, build: build.id, version: staged.deploy.version }
}
