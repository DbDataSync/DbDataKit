import { useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { useOutletContext } from 'react-router-dom'
import { ErrorBanner, StagedVersionView } from '@dbdatakit/shell'
import { buildApi } from '../api'
import type { CommandAnswer, PairedRunner, RunnerPublishData } from '../types'
import { emptyRunnerForm, runnerPublishBlock, runnerPublishFailure, runnerPublishRequest, stagedFrom, type RunnerForm } from './runnerPublish'

export interface RunnerPublishViewProps {
  runners: PairedRunner[] | undefined
  loading: boolean
  /** Why the list of runners could not be read, if it could not. */
  listError: unknown
  form: RunnerForm
  /** Whether this person may run `project publish` here (the commands list says so); undefined while it is not known. */
  allowed: boolean | undefined
  busy: boolean
  /** The last answer the command gave, shown until the next try. */
  answer: CommandAnswer<RunnerPublishData> | undefined
  /** The last call that failed in transport or was refused by role. */
  failure: unknown
  onForm: (form: RunnerForm) => void
  onPublish: () => void
}

/** The runner section of the Publish tab without its calls: choose the paired runner and channel, publish or promote, see the staged version and its tasks. Hook-free, so a test can draw it and press its button. */
export function RunnerPublishView(p: RunnerPublishViewProps) {
  const none = !p.loading && !p.listError && (p.runners?.length ?? 0) === 0
  const block = runnerPublishBlock(p.form, p.runners, p.allowed)
  const failed = runnerPublishFailure(p.failure, p.answer)
  const data = p.answer && p.answer.exit === 0 ? p.answer.document?.data : undefined
  const set = (change: Partial<RunnerForm>) => p.onForm({ ...p.form, ...change })
  return (
    <div data-testid="build-publish-runner">
      <div className="hint" style={{ marginBottom: 14 }} data-testid="build-publish-runner-about">
        Sends the project&apos;s committed files to a runner as a package: one task for each refresh plan, with its schedule and the connection slots it needs. Files the runner already has are not sent again. The version is <strong>staged</strong>: the runners prepare it and it
        activates once every runner of the group has it ready; activate it from the Runner screens. Until a plan is cut over, this server&apos;s scheduler keeps scheduling it and the runner does not.
        Needs the author role here and a publisher pairing with the runner.
      </div>
      <ErrorBanner error={p.listError} />
      {p.loading && <div className="empty">Loading…</div>}
      {none && <div className="empty" data-testid="build-publish-runner-none">This server is not paired with a runner. On the runner, <span className="mono">dbdatarunner server pair --max-role publisher</span> makes a single-use code; on this server&apos;s machine, <span className="mono">dbdatarunner package pair &lt;https address&gt; --code &lt;code&gt;</span> pairs it.</div>}
      {(p.runners?.length ?? 0) > 0 && (
        <>
          <div className="row" style={{ gap: 12, marginBottom: 10, flexWrap: 'wrap' }}>
            <label className="row" style={{ gap: 8 }}>
              <span className="hint">Do</span>
              <select className="input" value={p.form.mode} onChange={(e) => set({ mode: e.target.value as RunnerForm['mode'] })} data-testid="build-publish-runner-mode">
                <option value="publish">Publish this project</option>
                <option value="promote">Promote a channel&apos;s version</option>
              </select>
            </label>
            <label className="row" style={{ gap: 8 }}>
              <span className="hint">Runner</span>
              <select className="input" value={p.form.runner} onChange={(e) => set({ runner: e.target.value })} data-testid="build-publish-runner-server">
                <option value="">Choose…</option>
                {p.runners!.map((r) => <option key={r.name} value={r.name}>{r.name}</option>)}
              </select>
            </label>
            <label className="row" style={{ gap: 8 }}>
              <span className="hint">{p.form.mode === 'promote' ? 'To channel' : 'Channel'}</span>
              <input className="input mono" value={p.form.channel} placeholder="for example prod" onChange={(e) => set({ channel: e.target.value })} data-testid="build-publish-runner-channel" style={{ width: 140 }} />
            </label>
            {p.form.mode === 'promote' && (
              <label className="row" style={{ gap: 8 }}>
                <span className="hint">From channel</span>
                <input className="input mono" value={p.form.promoteFrom} placeholder="for example test" onChange={(e) => set({ promoteFrom: e.target.value })} data-testid="build-publish-runner-from" style={{ width: 140 }} />
              </label>
            )}
            <label className="row" style={{ gap: 8 }}>
              <span className="hint">Package</span>
              <input className="input mono" value={p.form.packageName} onChange={(e) => set({ packageName: e.target.value })} data-testid="build-publish-runner-package" style={{ width: 160 }} />
            </label>
          </div>
          <label style={{ display: 'block', marginBottom: 10 }}>
            <span className="hint">Connections of this channel, one <span className="mono">slot=connection</span> per line (a channel&apos;s bindings are its own; leave empty to keep what it has)</span>
            <textarea className="input mono" rows={2} value={p.form.bindText} onChange={(e) => set({ bindText: e.target.value })} data-testid="build-publish-runner-bind" style={{ width: '100%', marginTop: 4 }} />
          </label>
          {p.form.mode === 'publish' && (
            <div className="row" style={{ gap: 18, marginBottom: 10, flexWrap: 'wrap' }}>
              <label className="row" style={{ gap: 6 }}>
                <input type="checkbox" checked={p.form.serve} onChange={(e) => set({ serve: e.target.checked })} data-testid="build-publish-runner-serve-now" />
                <span className="hint">Serve it at once instead of staging it</span>
              </label>
              <label className="row" style={{ gap: 6 }}>
                <input type="checkbox" checked={p.form.runnerSchedules} onChange={(e) => set({ runnerSchedules: e.target.checked })} data-testid="build-publish-runner-schedules" />
                <span className="hint">Let the runner schedule the plans from the first activation</span>
              </label>
            </div>
          )}
          <div className="row" style={{ gap: 12, marginBottom: 14 }}>
            <button type="button" className="btn btn-primary" disabled={block !== null || p.busy} onClick={p.onPublish} data-testid="build-publish-runner-button">
              {p.busy ? (p.form.mode === 'promote' ? 'Promoting…' : 'Publishing…') : p.form.mode === 'promote' ? 'Promote' : 'Publish'}
            </button>
            {block !== null && !p.busy && <span className="hint" data-testid="build-publish-runner-why">{block}</span>}
          </div>
        </>
      )}
      {failed !== null && <div className="banner error" role="alert" data-testid="build-publish-runner-failed"><span className="mark">!</span><span>{failed}</span></div>}
      {data && <StagedVersionView data={stagedFrom(data)} testIdPrefix="build-publish-runner" />}
    </div>
  )
}

/** Publishing the project to a runner this server is paired with (plan 33, UE05), through the generic command route. */
export function RunnerPublish() {
  const { project } = useOutletContext<{ project: string }>()
  const [form, setForm] = useState<RunnerForm>(() => emptyRunnerForm(project))
  const runners = useQuery({ queryKey: ['build', 'runners'], queryFn: () => buildApi.runners() })
  const commands = useQuery({ queryKey: ['build', 'commands', project], queryFn: () => buildApi.commands(project) })
  const allowed = commands.data ? commands.data.find((c) => c.tool === 'project_publish')?.allowed ?? false : undefined
  const publish = useMutation({ mutationFn: () => buildApi.publishToRunner(project, runnerPublishRequest(form)) })
  return (
    <RunnerPublishView
      runners={runners.data} loading={runners.isLoading} listError={runners.error}
      form={form} allowed={allowed} busy={publish.isPending} answer={publish.data} failure={publish.error}
      onForm={(next) => { setForm(next); publish.reset() }}
      onPublish={() => publish.mutate()}
    />
  )
}
