import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useIsAdmin } from '@dbdatakit/shell'
import { gatewayApi } from '../api'
import type { ExportComponentState } from '../types'

const mb = (bytes: number) => `${Math.round(bytes / (1024 * 1024))} MB`

export interface ExportComponentViewProps {
  loading: boolean
  loadError: string | null
  state: ExportComponentState | undefined
  /** The Install call is in flight (a click, before the Gateway answers). */
  busy: boolean
  /** The Gateway refused the call itself, in words. */
  refusal: string | null
  allowed: boolean
  onInstall: (force: boolean) => void
}

/** What the card says and offers, from the Gateway's state of the component. Stateless: the page above it reads and polls. */
export function ExportComponentView(p: ExportComponentViewProps) {
  const s = p.state
  const running = s?.install.state === 'running'
  const whole = s?.installed && s.whole
  return (
    <div className="card" data-testid="export-component" data-state={s ? (running ? 'running' : whole ? (s.configured ? 'ready' : 'installed') : s.installed ? 'broken' : 'missing') : 'loading'}>
      <div className="card-body">
        <div className="card-title">PDF export component</div>
        {p.loading && <p className="hint" role="status" data-testid="export-loading">Reading the state of the export component…</p>}
        {p.loadError && <p className="hint" role="alert" data-testid="export-load-error">The export component could not be read: {p.loadError}</p>}
        {s && (
          <>
            <p data-testid="export-summary">{s.summary}</p>
            {!s.supported && <p className="hint" data-testid="export-unsupported">This platform has no pinned browser. Set views.export to an installed browser (mode: installed browser) above.</p>}
            {s.problems.length > 0 && (
              <ul data-testid="export-problems">{s.problems.map((m, i) => <li key={i} className="mono">{m}</li>)}</ul>
            )}
            {running && (
              <p role="status" data-testid="export-progress">
                {s.install.step}{s.install.total > 0 && <> <progress max={s.install.total} value={s.install.bytes} /> {mb(s.install.bytes)} of {mb(s.install.total)}</>}
              </p>
            )}
            {s.install.state === 'failed' && s.install.message && <p role="alert" className="mono" data-testid="export-failed">{s.install.message}</p>}
            {s.install.state === 'done' && s.install.message && <p role="status" data-testid="export-done" data-restart={s.install.restart_needed ? 'true' : 'false'}>{s.install.message}</p>}
            {p.refusal && <p role="alert" data-testid="export-refused">{p.refusal}</p>}
            {s.supported && !whole && !running && (
              <div className="row" style={{ gap: 10, alignItems: 'center', flexWrap: 'wrap' }}>
                <button type="button" className="btn btn-primary" disabled={!p.allowed || p.busy} title={p.allowed ? 'Download the component of this release, check it and set views.export' : 'Installing is for administrators'} onClick={() => p.onInstall(s.installed)} data-testid="export-install">
                  {s.installed ? 'Install again' : 'Install'}
                </button>
                <span className="hint">The browser, the bundled fonts and the print page (about 420 MB) are downloaded from this release and checked against its checksums and their pins; nothing is installed if one does not match. Saving views.export is a commit of the configuration, and the Gateway is restarted to use it.</span>
              </div>
            )}
            {whole && !running && s.install.state !== 'done' && <p className="hint" data-testid="export-note">Release {s.release ?? 'unknown'}, Chromium {s.chromium}, bundled fonts {s.fonts}, in {s.directory}.</p>}
          </>
        )}
      </div>
    </div>
  )
}

/** The card on the server settings screen: reads the state, polls while an install runs, and starts the install through the Gateway (admin only; audited there). */
export function ExportComponentCard() {
  const isAdmin = useIsAdmin()
  const queryClient = useQueryClient()
  const state = useQuery({
    queryKey: ['gateway', 'export-component'], queryFn: gatewayApi.exportComponent.read, enabled: isAdmin, retry: false, refetchOnWindowFocus: false,
    refetchInterval: (q) => (q.state.data?.install.state === 'running' ? 1000 : false),
  })
  const install = useMutation({
    mutationFn: (force: boolean) => gatewayApi.exportComponent.install(force),
    onSuccess: (next) => queryClient.setQueryData(['gateway', 'export-component'], next),
    onSettled: async () => { await queryClient.invalidateQueries({ queryKey: ['gateway', 'export-component'] }); await queryClient.invalidateQueries({ queryKey: ['gateway', 'settings'] }); await queryClient.invalidateQueries({ queryKey: ['gateway', 'history'] }) },
  })
  if (!isAdmin) return null
  return (
    <ExportComponentView loading={state.isPending} loadError={state.error ? (state.error instanceof Error ? state.error.message : String(state.error)) : null} state={state.data}
      busy={install.isPending} refusal={install.error ? (install.error instanceof Error ? install.error.message : String(install.error)) : null} allowed={isAdmin} onInstall={(force) => install.mutate(force)} />
  )
}
