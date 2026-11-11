import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'
import { runnerApi } from '../api'
import { signaturesText } from '../bindings'
import { ConfirmDialog } from './Dialogs'

export interface PublishingSettingsViewProps {
  /** Null while the setting is not known. */
  requireSignatures: boolean | null
  loading: boolean
  error: unknown
  canEdit: boolean
  onToggle: () => void
}

/** The runner's publishing settings without their calls: whether a version needs a valid signature, and the switch for an administrator. Hook-free so a test can draw it and press its button. */
export function PublishingSettingsView(p: PublishingSettingsViewProps) {
  return (
    <div className="card" data-testid="runner-settings">
      <div className="card-body">
        <div className="card-title">Publishing</div>
        <ErrorBanner error={p.error} />
        {p.loading && <div className="empty" data-testid="runner-settings-loading">Loading...</div>}
        {p.requireSignatures !== null && (
          <>
            <div data-testid="runner-require-signatures" data-value={p.requireSignatures}>Require signatures: <strong>{p.requireSignatures ? 'on' : 'off'}</strong></div>
            <div className="hint" data-testid="runner-require-signatures-text">{signaturesText(p.requireSignatures)}</div>
            <div className="row" style={{ gap: 12, marginTop: 8 }}>
              <button type="button" className="btn" disabled={!p.canEdit} onClick={p.onToggle} data-testid="runner-require-signatures-toggle">{p.requireSignatures ? 'Turn off' : 'Turn on'}</button>
              {!p.canEdit && <span className="hint" data-testid="runner-require-signatures-why">Changing it needs the admin role on this server.</span>}
            </div>
          </>
        )}
      </div>
    </div>
  )
}

/** The publishing settings of the runner (plan 33, UE08): who may change them is the runner's rule (an administrator), and a change is audited there. */
export function PublishingSettings({ canEdit }: { canEdit: boolean }) {
  const client = useQueryClient()
  const settings = useQuery({ queryKey: ['runner', 'settings'], queryFn: runnerApi.settings, refetchInterval: 10000 })
  const [asking, setAsking] = useState(false)
  const on = settings.data?.require_signatures ?? null
  return (
    <>
      <PublishingSettingsView requireSignatures={on} loading={settings.isLoading} error={settings.error} canEdit={canEdit} onToggle={() => setAsking(true)} />
      {asking && on !== null && (
        <ConfirmDialog
          label={on ? 'Turn off' : 'Turn on'}
          text={on ? 'Versions are accepted unsigned again and recorded as unsigned.' : signaturesText(true)}
          go={() => runnerApi.setRequireSignatures(!on)}
          onCancel={() => setAsking(false)}
          onDone={() => { setAsking(false); void client.invalidateQueries({ queryKey: ['runner'] }) }}
        />
      )}
    </>
  )
}
