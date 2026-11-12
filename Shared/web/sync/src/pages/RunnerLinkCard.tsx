import { useState } from 'react'
import { ErrorBanner, Field, useIsAdmin } from '@dbdatakit/shell'
import { useRunnerLink, useSaveRunnerLink } from '../api/hooks'
import type { RunnerLink } from '../api/types'

/**
 * The paired runner the replications are published to, the channel that serves them and the package they are in (plan 28c E12). Shown on the Runner screen and on a replication's Runner tab: one stored link
 * (`runner-owners.json` beside Sync's state), set by an administrator.
 */
export function RunnerLinkCard() {
  const isAdmin = useIsAdmin()
  const link = useRunnerLink()
  const saveLink = useSaveRunnerLink()
  // null is "what the server has"; a link is an unsaved edit of it
  const [edit, setEdit] = useState<RunnerLink | null>(null)
  const draft: RunnerLink = edit ?? (link.data ? { ...link.data, clientDirectory: link.data.clientDirectory ?? '' } : { server: '', channel: '', package: '', clientDirectory: '' })
  const setDraft = setEdit
  // the fields wait for the stored link: text typed (or filled by a test) while it is still loading is inserted into the empty draft and then lands beside the stored value that arrives
  const loading = link.isPending
  const complete = draft.server.trim() !== '' && draft.channel.trim() !== '' && draft.package.trim() !== ''

  return (
    <div className="card" data-testid="runner-link-card">
      <div className="card-head tight"><span className="card-title sm">The runner</span></div>
      <p className="hint">The paired runner the replications are published to, the channel that serves them and the package they are in.</p>
      <div style={{ display: 'grid', gap: 8, gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))' }}>
        <Field label="Server (name or address)"><input className="input" value={draft.server} disabled={loading} onChange={(e) => setDraft({ ...draft, server: e.target.value })} data-testid="runner-link-server" /></Field>
        <Field label="Channel"><input className="input" value={draft.channel} disabled={loading} onChange={(e) => setDraft({ ...draft, channel: e.target.value })} data-testid="runner-link-channel" /></Field>
        <Field label="Package"><input className="input" value={draft.package} disabled={loading} onChange={(e) => setDraft({ ...draft, package: e.target.value })} data-testid="runner-link-package" /></Field>
        <Field label="Client directory (optional)"><input className="input" value={draft.clientDirectory ?? ''} disabled={loading} onChange={(e) => setDraft({ ...draft, clientDirectory: e.target.value })} data-testid="runner-link-client-dir" /></Field>
      </div>
      <div style={{ marginTop: 12 }}>
        <button type="button" className="btn btn-sm" disabled={!isAdmin || !complete || saveLink.isPending} title={!isAdmin ? 'Setting the runner needs the admin role.' : !complete ? 'The server, channel and package are all needed.' : undefined}
          onClick={() => saveLink.mutate({ ...draft, clientDirectory: draft.clientDirectory?.trim() ? draft.clientDirectory : null }, { onSuccess: () => setEdit(null) })} data-testid="runner-link-save">
          Save the runner
        </button>
      </div>
      {saveLink.error && <ErrorBanner error={saveLink.error} />}
    </div>
  )
}
