import { useState } from 'react'
import type { PublishTarget } from '../api'

/** Why "Preview with data" cannot be pressed yet, in words, or null when it can: the person writes in the workspace, and says which publisher key (a Gateway) and which instance of it the preview is of. */
export function previewDataButton({ canWrite, status, target, instance, busy }: { canWrite: boolean; status: string; target: string; instance: string; busy: boolean }): { enabled: boolean; why: string | null } {
  if (!canWrite) return { enabled: false, why: 'Sending a preview needs the author role in this project.' }
  if (status !== 'succeeded') return { enabled: false, why: 'Only a build that succeeded is sent.' }
  if (busy) return { enabled: false, why: 'Sending the preview...' }
  if (target.trim() === '') return { enabled: false, why: 'Name the publisher key of the Gateway the preview goes to.' }
  if (instance.trim() === '') return { enabled: false, why: 'Name the instance on that Gateway the preview is of: the people who may open it may open its preview.' }
  return { enabled: true, why: null }
}

/**
 * "Preview with data" (plan 22a GP35d): the build is sent to the Gateway's preview channel and a preview of an instance runs it. The person opens it at the Gateway, signed in as themselves: it reads the data the instance reads
 * and shows the semantic figures they may see, and it writes nothing (the changes are refused there). It sends nothing itself: `onSend` gets the target and the instance.
 */
export function PreviewDataForm({ canWrite, status, targets = [], busy, onSend, initialTarget = '', initialInstance = '' }: {
  canWrite: boolean; status: string; targets?: readonly PublishTarget[]; busy: boolean
  onSend: (target: string, instance: string) => void; initialTarget?: string; initialInstance?: string
}) {
  const [target, setTarget] = useState(initialTarget)
  const [instance, setInstance] = useState(initialInstance)
  const state = previewDataButton({ canWrite, status, target, instance, busy })
  return (
    <form className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap', marginTop: 10 }} data-testid="wsp-preview-data-form" onSubmit={(e) => { e.preventDefault(); if (state.enabled) onSend(target.trim(), instance.trim()) }}>
      <input className="input mono" list="wsp-preview-targets" aria-label="Preview on" placeholder="publisher key" value={target} onChange={(e) => setTarget(e.target.value)} data-testid="wsp-preview-data-target" />
      <datalist id="wsp-preview-targets">{targets.filter((t) => t.accepts !== false).map((t) => <option key={t.name} value={t.name}>{t.gateway ?? 'not paired'}</option>)}</datalist>
      <input className="input mono" style={{ width: 170 }} aria-label="Instance" placeholder="instance" value={instance} onChange={(e) => setInstance(e.target.value)} data-testid="wsp-preview-data-instance" />
      <button type="submit" className="btn" disabled={!state.enabled} title={state.why ?? undefined} data-testid="wsp-preview-data">Preview with data</button>
      <span className="hint" data-testid="wsp-preview-data-why">{state.why ?? 'Sends this build to the Gateway as a preview: it reads the instance\'s data as you and changes nothing.'}</span>
    </form>
  )
}
