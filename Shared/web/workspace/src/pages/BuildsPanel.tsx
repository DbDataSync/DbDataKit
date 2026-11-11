import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, ErrorBanner, Link } from '@dbdatakit/shell'
import { workspaceApi, type BuildDetail, type BuildSummary, type PublishAnswer, type PublishOptions } from '../api'
import { buildBusy, buildButton, buildWord, diagnosticLine, makeLabel, notIsolated, publishedLine } from '../model'
import { PublishForm } from './PublishForm'

const when = (iso: string | null) => (iso ? new Date(iso).toLocaleString() : '')

/** What the server needs to build and whether it has it: the package store, Bun, the isolation. A server that cannot build says what to set, in its own sentences. */
function Readiness() {
  const readiness = useQuery({ queryKey: ['workspace', 'readiness'], queryFn: workspaceApi.readiness, staleTime: 10_000 })
  const r = readiness.data
  return (
    <div data-testid="wsp-readiness" data-ready={r ? String(r.ready) : undefined}>
      <ErrorBanner error={readiness.error} />
      {r && !r.ready && (
        <div className="empty" data-testid="wsp-build-problems">
          <div>This server cannot build an app yet:</div>
          <ul>{r.problems.map((p, i) => <li key={i} data-testid="wsp-build-problem">{p}</li>)}</ul>
        </div>
      )}
      {r?.ready && r.package_store && (
        <div className="hint" data-testid="wsp-build-store">
          Builds use the package store {r.package_store.folder} ({r.package_store.packages} packages) and install nothing. {r.isolation.setting === 'required'
            ? 'Each build runs in a network namespace with no route.'
            : r.isolation.available ? 'Builds run in a network namespace with no route when this machine can make one.' : 'This machine cannot make a network namespace, so builds run without network isolation (the server is set to allow that).'}
        </div>
      )}
    </div>
  )
}

/** The refusal of a publish, in the server's words, and the steps it got through. */
function PublishFailure({ error }: { error: unknown }) {
  const steps = error instanceof ApiError && typeof error.body === 'object' && error.body && Array.isArray((error.body as { steps?: unknown }).steps) ? (error.body as { steps: string[] }).steps : []
  return (
    <div data-testid="wsp-publish-refused">
      <ErrorBanner error={error} />
      {steps.length > 0 && <div className="hint">Got as far as: {steps.join(' ')}</div>}
    </div>
  )
}

/** One build: what it said (notes, diagnostics with file and line), its preview and its publish. */
function BuildDetailView({ project, id, buildId, canWrite, kind }: { project: string; id: string; buildId: string; canWrite: boolean; kind: string }) {
  const client = useQueryClient()
  const [previewing, setPreviewing] = useState(false)
  const app = kind === 'app'
  const detail = useQuery<BuildDetail>({
    queryKey: ['workspace', 'build', project, id, buildId],
    queryFn: () => workspaceApi.build(project, id, buildId),
    refetchInterval: (q) => (q.state.data && buildBusy(q.state.data.build.status) ? 1200 : false),
  })
  const targets = useQuery({ queryKey: ['workspace', 'targets', project], queryFn: () => workspaceApi.publishTargets(project), retry: false })
  const publish = useMutation<PublishAnswer, Error, { target: string; note: string; options: PublishOptions }>({
    mutationFn: (p) => workspaceApi.publish(project, id, buildId, p.target, p.note, p.options),
    onSettled: () => client.invalidateQueries({ queryKey: ['workspace', 'build', project, id, buildId] }),
  })
  const build = detail.data?.build
  const result = detail.data?.result ?? null
  if (!build) return <div data-testid="wsp-build-detail"><ErrorBanner error={detail.error} /></div>
  return (
    <div className="card" data-testid="wsp-build-detail" data-build={build.id} data-status={build.status}>
      <div className="card-body">
        <div className="row" style={{ gap: 10, alignItems: 'center', flexWrap: 'wrap' }}>
          <span className="mono" data-testid="wsp-build-status">{buildWord(build.status)}</span>
          {build.message && <span data-testid="wsp-build-message">{build.message}</span>}
        </div>
        {buildBusy(build.status) && <div className="hint" data-testid="wsp-build-wait">{app ? 'Building: Bun runs with no network from the package store. This page follows it.' : 'Checking the draft with the product\'s own program and making what is published. This page follows it.'}</div>}
        {result && (
          <>
            {result.uncommitted_changes && <div className="hint" data-testid="wsp-build-dirty">Built from changes that are not committed yet.</div>}
            {result.status === 'succeeded' && app && (
              <div className="hint" data-testid="wsp-build-made">{result.app} {result.version}: {result.files} files, {result.bundled?.length ?? 0} bundled, {result.seconds} s.</div>
            )}
            {result.status === 'succeeded' && !app && result.info && (
              <div className="hint" data-testid="wsp-build-made" data-kind={result.kind ?? ''}>
                {Object.entries(result.info).map(([k, v]) => <div key={k} data-testid="wsp-build-info" data-key={k}>{k}: {v}</div>)}
              </div>
            )}
            {notIsolated(result.notes ?? []) && (
              <div className="empty" data-testid="wsp-build-not-isolated">Not isolated: this build ran without a network namespace (this machine cannot make one and the server is set to build anyway). Nothing was installed and no proxy was configured, but the bundler could have opened a socket.</div>
            )}
            {result.isolated && <div className="hint" data-testid="wsp-build-isolated">The bundler ran in a network namespace with no route.</div>}
            {result.diagnostics.length > 0 && (
              <ul data-testid="wsp-build-diagnostics">
                {result.diagnostics.map((d, i) => <li key={i} className="mono" data-testid="wsp-build-diagnostic" data-file={d.file ?? ''} data-line={d.line ?? ''}>{diagnosticLine(d)}</li>)}
              </ul>
            )}
            {(result.notes?.length ?? 0) > 0 && <details><summary className="hint">What the build said</summary>{result.notes!.map((n, i) => <div key={i} className="hint">{n}</div>)}</details>}
          </>
        )}
        {build.status === 'succeeded' && (
          <>
            {app && (
              <>
                <div className="row" style={{ gap: 8, margin: '10px 0', flexWrap: 'wrap', alignItems: 'center' }}>
                  <button type="button" className="btn" data-testid="wsp-preview-toggle" onClick={() => setPreviewing((v) => !v)}>{previewing ? 'Close the preview' : 'Preview'}</button>
                  <span className="hint">The preview is read only: the page has no network and shows no data.</span>
                </div>
                {previewing && (
                  <iframe title="Preview of the built app" data-testid="wsp-preview-frame" sandbox="allow-scripts" src={workspaceApi.previewUrl(project, id, build.id)}
                    style={{ width: '100%', height: 420, border: '1px solid var(--border, #ccc)', background: '#fff' }} />
                )}
              </>
            )}
            <PublishForm canWrite={canWrite} status={build.status} targets={targets.data ?? []} models={kind === 'store'} busy={publish.isPending} onPublish={(target, note, options) => publish.mutate({ target, note, options })} />
            {publish.isPending && <div className="hint" data-testid="wsp-publish-progress">Publishing: the server reads the key and sends it; the Gateway or Runner checks it and stores the version...</div>}
            {publish.isError && <PublishFailure error={publish.error} />}
            {publish.isSuccess && (
              <div data-testid="wsp-published" data-version={publish.data.published.version ?? ''}>
                <div className="empty" data-testid="wsp-published-line">{publishedLine(publish.data.published)}</div>
                <ol className="hint">{publish.data.steps.map((s, i) => <li key={i} data-testid="wsp-publish-step">{s}</li>)}</ol>
              </div>
            )}
          </>
        )}
        {(detail.data?.publishes.length ?? 0) > 0 && (
          <div data-testid="wsp-publishes">
            <div className="hint" style={{ marginTop: 8 }}>Published from this build</div>
            {detail.data!.publishes.map((p, i) => (
              <div key={i} data-testid="wsp-publish-record" data-ok={String(p.ok)}>
                {p.by} to {p.target} {when(p.at)}: {p.ok ? (p.answer ? publishedLine(p.answer) : '') : p.error}
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  )
}

/** The builds of a draft: build it, follow the build, read what it said, preview it (read only) and publish it to a Gateway or a Runner (by what its kind makes). A merged workspace (`closed`) keeps its builds: they can still be previewed and published. */
export function BuildsPanel({ project, id, canWrite, closed = false }: { project: string; id: string; canWrite: boolean; closed?: boolean }) {
  const client = useQueryClient()
  const [selected, setSelected] = useState<string | null>(null)
  const projects = useQuery({ queryKey: ['workspace', 'projects'], queryFn: workspaceApi.projects, staleTime: 30_000 })
  const kind = projects.data?.find((p) => p.id === project)?.kind ?? 'app'
  const app = kind === 'app'
  const readiness = useQuery({ queryKey: ['workspace', 'readiness'], queryFn: workspaceApi.readiness, staleTime: 10_000, enabled: app })
  const builds = useQuery<BuildSummary[]>({
    queryKey: ['workspace', 'builds', project, id],
    queryFn: () => workspaceApi.builds(project, id),
    refetchInterval: (q) => (q.state.data?.some((b) => buildBusy(b.status)) ? 1200 : false),
  })
  const ask = useMutation({
    mutationFn: () => workspaceApi.requestBuild(project, id),
    onSuccess: async (b) => { setSelected(b.id); await client.invalidateQueries({ queryKey: ['workspace', 'builds', project, id] }) },
  })
  const busy = (builds.data ?? []).some((b) => buildBusy(b.status)) || ask.isPending
  const button = buildButton({ canWrite, ready: app ? readiness.data?.ready ?? false : true, problems: readiness.data?.problems ?? [], busy, closed })
  const shown = selected ?? builds.data?.[0]?.id ?? null
  return (
    <div data-testid="wsp-builds">
      {app && <Readiness />}
      <div className="row" style={{ gap: 10, margin: '8px 0', alignItems: 'center', flexWrap: 'wrap' }}>
        <button type="button" className="btn btn-primary" disabled={!button.enabled} title={button.why ?? undefined} data-testid="wsp-build" onClick={() => ask.mutate()}>{makeLabel(kind)}</button>
        {button.why && (closed || !app || !readiness.data?.problems.length) && <span className="hint" data-testid="wsp-build-why">{button.why}</span>}
        <span className="hint">{app ? 'Builds' : 'Checks and makes'} what is in this workspace now, committed or not. <Link to="/keys" data-testid="wsp-keys-link">Publisher keys</Link></span>
      </div>
      <ErrorBanner error={ask.error ?? builds.error} />
      {builds.data?.length === 0 && <div className="empty" data-testid="wsp-builds-none">{app ? 'This workspace has not been built.' : 'Nothing has been made from this workspace yet.'}</div>}
      {(builds.data ?? []).map((b) => (
        <button key={b.id} type="button" className={`grid-row ${shown === b.id ? 'active' : ''}`} style={{ width: '100%', textAlign: 'left', gap: 14 }} data-testid="wsp-build-row" data-build={b.id} data-status={b.status} aria-current={shown === b.id} onClick={() => setSelected(b.id)}>
          <span className="mono">{buildWord(b.status)}</span>
          <span className="mono">{b.commit?.slice(0, 8) ?? 'no commit'}</span>
          <span>{b.requested_by}</span>
          <span className="hint">{when(b.requested)}</span>
          <span className="hint">{b.message ?? ''}</span>
        </button>
      ))}
      {shown && <BuildDetailView key={shown} project={project} id={id} buildId={shown} canWrite={canWrite} kind={kind} />}
    </div>
  )
}
