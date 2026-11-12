import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'
import { runnerApi, type PostureState } from '../api'
import { choosableSettings, emptyPostureForm, postureBlock, type PostureForm } from '../operator'
import { when } from './format'

export interface PostureViewProps {
  posture: PostureState | undefined
  loading: boolean
  error: unknown
  /** Whether the person is offered the controls: the runner's admin role, which the server checks again. */
  canEdit: boolean
  form: PostureForm
  busy: boolean
  /** What the last change did, shown until the next try. */
  done: string | null
  failure: unknown
  onForm: (form: PostureForm) => void
  onChoose: (form: PostureForm) => void
  onRevoke: (setting: string, scope: string) => void
}

/**
 * The defaults of the runner an administrator may loosen on purpose (plan 28a R19), each with what it is today and what loosening it changes; the choices recorded so far with who made them; and the form that records one: a package (or every
 * package), the effect shown, and a confirmation that it is accepted. A choice and its revoke are on the audit. Hook-free so a test can draw it and press its buttons.
 */
export function PostureView(p: PostureViewProps) {
  const settings = p.posture ? choosableSettings(p.posture.settings) : []
  const chosen = settings.find((s) => s.name === p.form.setting)
  const block = postureBlock(p.form)
  return (
    <div className="card" data-testid="runner-posture">
      <div className="card-body">
        <div className="card-title">Security posture</div>
        <div className="hint">Each default below keeps a package inside the runner&apos;s limits. Loosening one is allowed, never silent: it is recorded here with who chose it and shown on the audit.</div>
        <ErrorBanner error={p.error ?? p.failure} />
        {p.loading && <div className="empty" data-testid="runner-posture-loading">Loading…</div>}
        {p.posture && p.posture.choices.length === 0 && <div className="hint" style={{ marginTop: 6 }} data-testid="runner-posture-none">No default is loosened: every package runs inside the defaults.</div>}
        {(p.posture?.choices ?? []).map((c) => (
          <div key={`${c.setting}/${c.scope}`} className="row" style={{ gap: 10, flexWrap: 'wrap', alignItems: 'baseline', marginTop: 6 }} data-testid="runner-posture-choice" data-setting={c.setting} data-scope={c.scope}>
            <strong className="mono">{c.setting}</strong>
            <span>for {c.scope === p.posture!.all_packages ? 'every package' : c.scope}</span>
            <span className="hint">chosen by {c.chosen_by}, {when(c.chosen_at)}{c.note ? `: ${c.note}` : ''}</span>
            <button type="button" className="btn btn-sm" disabled={!p.canEdit || p.busy} onClick={() => p.onRevoke(c.setting, c.scope)} data-testid="runner-posture-revoke">Take back</button>
          </div>
        ))}
        {p.posture && p.canEdit && (
          <div style={{ marginTop: 12 }} data-testid="runner-posture-form">
            <div className="row" style={{ gap: 10, flexWrap: 'wrap' }}>
              <label className="row" style={{ gap: 6 }}>
                <span className="hint">Default to loosen</span>
                <select className="input" value={p.form.setting} onChange={(e) => p.onForm({ ...p.form, setting: e.target.value, confirmed: false })} data-testid="runner-posture-setting">
                  {settings.map((s) => <option key={s.name} value={s.name}>{s.name}</option>)}
                </select>
              </label>
              <label className="row" style={{ gap: 6 }}>
                <span className="hint">For package</span>
                <input className="input mono" value={p.form.scope} placeholder={`a package name, or ${p.posture.all_packages}`} onChange={(e) => p.onForm({ ...p.form, scope: e.target.value, confirmed: false })} data-testid="runner-posture-scope" style={{ width: 220 }} />
              </label>
              <label className="row" style={{ gap: 6 }}>
                <span className="hint">Note</span>
                <input className="input" value={p.form.note} onChange={(e) => p.onForm({ ...p.form, note: e.target.value })} data-testid="runner-posture-note" style={{ width: 200 }} />
              </label>
            </div>
            {chosen && (
              <div style={{ marginTop: 8 }} data-testid="runner-posture-effect">
                <div className="hint">Today: {chosen.default}.</div>
                <div className="banner" role="note"><span>{chosen.effect}</span></div>
              </div>
            )}
            <label className="row" style={{ gap: 6, marginTop: 6 }}>
              <input type="checkbox" checked={p.form.confirmed} onChange={(e) => p.onForm({ ...p.form, confirmed: e.target.checked })} data-testid="runner-posture-confirm" />
              <span>I accept this effect and my name goes on the record</span>
            </label>
            <div className="row" style={{ gap: 10, marginTop: 6 }}>
              <button type="button" className="btn" disabled={block !== null || p.busy} onClick={() => p.onChoose(p.form)} data-testid="runner-posture-choose">{p.busy ? 'Recording…' : 'Record the choice'}</button>
              {block && <span className="hint" data-testid="runner-posture-why">{block}</span>}
            </div>
          </div>
        )}
        {p.posture && !p.canEdit && <div className="hint" style={{ marginTop: 6 }} data-testid="runner-posture-role">Changing the posture needs the admin role on this server.</div>}
        {p.done && <div className="hint" style={{ marginTop: 6 }} data-testid="runner-posture-done">{p.done}</div>}
      </div>
    </div>
  )
}

/** The posture of the runner (plan 28a R19): read for anyone who may read the runner, changed by an administrator, audited there. */
export function PostureCard({ canEdit }: { canEdit: boolean }) {
  const client = useQueryClient()
  const posture = useQuery({ queryKey: ['runner', 'posture'], queryFn: runnerApi.posture, refetchInterval: 10_000 })
  const [form, setForm] = useState<PostureForm | null>(null)
  const [done, setDone] = useState<string | null>(null)
  const change = useMutation({
    mutationFn: (body: { setting: string; scope: string; confirm?: boolean; revoke?: boolean; note?: string }) => runnerApi.choosePosture(body),
    onSuccess: (_, body) => { setDone(body.revoke ? `${body.setting} for ${body.scope} is taken back: the default applies again.` : `${body.setting} for ${body.scope} is recorded under your name.`); setForm(null); client.invalidateQueries({ queryKey: ['runner'] }) },
  })
  const current = form ?? emptyPostureForm(posture.data?.settings ?? [])
  return (
    <PostureView
      posture={posture.data} loading={posture.isLoading} error={posture.error} canEdit={canEdit} form={current} busy={change.isPending} done={done} failure={change.error}
      onForm={(next) => { setForm(next); setDone(null); change.reset() }}
      onChoose={(f) => { setDone(null); change.mutate({ setting: f.setting, scope: f.scope.trim(), confirm: true, note: f.note.trim() || undefined }) }}
      onRevoke={(setting, scope) => { setDone(null); change.mutate({ setting, scope, revoke: true }) }}
    />
  )
}
