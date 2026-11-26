import { useState } from 'react'
import type { DeployTarget } from '../deploys'

/** The Gateways a preview can go to: those the console manages that take an app (every Gateway, when the kind's routes are read). */
export function previewTargets(targets: readonly DeployTarget[]): DeployTarget[] {
  return targets.filter((t) => t.product === 'gateway' && t.accepts)
}

/** Why "Preview with data" cannot be pressed yet, in words, or null when it can: the person writes in the workspace, and chooses a Gateway the console manages and the instance of it the preview is of. */
export function previewDataButton({ canWrite, status, target, targets, instance, busy }: { canWrite: boolean; status: string; target: string; targets: readonly DeployTarget[]; instance: string; busy: boolean }): { enabled: boolean; why: string | null } {
  if (!canWrite) return { enabled: false, why: 'Sending a preview needs the author role in this project.' }
  if (status !== 'succeeded') return { enabled: false, why: 'Only a build that succeeded is sent.' }
  if (busy) return { enabled: false, why: 'Sending the preview...' }
  const offered = previewTargets(targets)
  if (target === '') return { enabled: false, why: offered.length === 0 ? 'The console manages no Gateway to send a preview to: an administrator adds one under Servers.' : 'Choose the Gateway the preview goes to.' }
  const known = offered.find((t) => t.id === target)
  if (!known) return { enabled: false, why: 'Choose a Gateway the console manages.' }
  if (!known.reachable) return { enabled: false, why: `${known.name} is not answering the console.` }
  if (instance.trim() === '') return { enabled: false, why: 'Name the instance on that Gateway the preview is of: the people who may open it may open its preview.' }
  return { enabled: true, why: null }
}

/**
 * "Preview with data": the console sends the build, as the signed-in person, to a Gateway's preview channel and points the preview of one of its instances at it. The person opens it at the Gateway, signed in as themselves:
 * it reads the data the instance reads and shows the semantic figures they may see, and it writes nothing (the changes are refused there). It sends nothing itself: `onSend` gets the Gateway's id and the instance.
 */
export function PreviewDataForm({ canWrite, status, targets = [], busy, onSend, initialTarget = '', initialInstance = '' }: {
  canWrite: boolean; status: string; targets?: readonly DeployTarget[]; busy: boolean
  onSend: (target: string, instance: string) => void; initialTarget?: string; initialInstance?: string
}) {
  const [chosen, setTarget] = useState(initialTarget)
  const [instance, setInstance] = useState(initialInstance)
  const offered = previewTargets(targets)
  // the only Gateway is chosen without asking
  const target = chosen !== '' ? chosen : offered.length === 1 ? offered[0]!.id : ''
  const state = previewDataButton({ canWrite, status, target, targets, instance, busy })
  return (
    <form className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap', marginTop: 10 }} data-testid="wsp-preview-data-form" onSubmit={(e) => { e.preventDefault(); if (state.enabled) onSend(target, instance.trim()) }}>
      <select className="input" aria-label="Preview on" value={target} onChange={(e) => setTarget(e.target.value)} data-testid="wsp-preview-data-target">
        <option value="">{offered.length === 0 ? 'No Gateway to preview on' : 'Choose a Gateway'}</option>
        {offered.map((t) => <option key={t.id} value={t.id}>{t.name}{t.reachable ? '' : ' (not answering)'}</option>)}
      </select>
      <input className="input mono" style={{ width: 170 }} aria-label="Instance" placeholder="instance" value={instance} onChange={(e) => setInstance(e.target.value)} data-testid="wsp-preview-data-instance" />
      <button type="submit" className="btn" disabled={!state.enabled} title={state.why ?? undefined} data-testid="wsp-preview-data">Preview with data</button>
      <span className="hint" data-testid="wsp-preview-data-why">{state.why ?? 'The console sends this build to the Gateway as a preview, as you: it reads the instance\'s data as you and changes nothing.'}</span>
    </form>
  )
}
