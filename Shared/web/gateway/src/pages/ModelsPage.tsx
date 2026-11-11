import { useState, type ReactNode } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { ErrorBanner } from '@dbdatakit/shell'
import { gatewayApi } from '../api'
import { GatewayShell } from '../GatewayShell'
import { ownerLabel, removeWarning, serveBlock, servedLabel, sortModels } from '../models'
import type { EntityModelDetail, EntityModelRow, ModelVersionRow, SemanticModelDetail, SemanticModelRow } from '../types'

const when = (iso: string | null) => (iso ? new Date(iso).toLocaleString() : '-')
const wrap = { whiteSpace: 'normal', overflowWrap: 'anywhere' } as const

function Section({ title, note, testId, children }: { title: string; note: string; testId: string; children: ReactNode }) {
  return (
    <section style={{ marginTop: 24 }} data-testid={testId}>
      <h2 className="card-title">{title}</h2>
      <p className="hint" style={{ margin: '2px 0 8px' }}>{note}</p>
      {children}
    </section>
  )
}

function VersionList({ versions, current, canServe, busy, onServe, prefix }: { versions: ModelVersionRow[]; current: number | null; canServe: boolean; busy: boolean; onServe: (version: number) => void; prefix: string }) {
  return (
    <div className="card flush" style={{ margin: '6px 0 10px' }} data-testid={`${prefix}-versions`}>
      <div className="grid-head" style={{ gridTemplateColumns: '0.6fr 1.6fr 1.6fr 1.2fr', gap: 14 }}><span>Version</span><span>Published by</span><span>When</span><span /></div>
      {[...versions].sort((a, b) => b.version - a.version).map((v) => (
        <div key={v.version} className="grid-row auto" style={{ gridTemplateColumns: '0.6fr 1.6fr 1.6fr 1.2fr', gap: 14 }} data-testid={`${prefix}-version-${v.version}`}>
          <span>{v.version}{v.version === current ? ' (served)' : ''}</span><span style={wrap}>{v.published_by ?? '-'}</span><span>{when(v.published_at)}</span>
          <span>{canServe && v.version !== current && <button type="button" className="btn sm" disabled={busy} onClick={() => onServe(v.version)} data-testid={`${prefix}-serve-${v.version}`}>Serve this version</button>}</span>
        </div>
      ))}
    </div>
  )
}

export interface SemanticModelsViewProps {
  rows: SemanticModelRow[] | undefined
  loading: boolean
  listError: unknown
  opened: string | null
  detail: SemanticModelDetail | undefined
  detailError: unknown
  confirming: string | null
  busy: boolean
  /** What the last change came to, in words, or the server's refusal. */
  notice: { text: string; bad: boolean } | null
  onOpen: (name: string | null) => void
  onServe: (name: string, version: number) => void
  onAskRemove: (name: string | null) => void
  onRemove: (name: string) => void
}

/** The semantic models the group serves: what is current, what this instance serves, who published it, every version, serve an earlier one, remove. */
export function SemanticModelsView(p: SemanticModelsViewProps) {
  const rows = sortModels(p.rows ?? [])
  return (
    <div data-testid="models-semantic-view">
      <ErrorBanner error={p.listError} />
      {p.loading && <div className="empty">Loading…</div>}
      {p.notice && <div className={p.notice.bad ? 'banner error' : 'hint'} role={p.notice.bad ? 'alert' : undefined} style={{ marginBottom: 8, ...wrap }} data-testid="models-notice">{p.notice.text}</div>}
      {p.rows && (
        <div className="card flush" data-testid="models-semantic-table">
          <div className="grid-head" style={{ gridTemplateColumns: '1.3fr 0.7fr 3fr 2fr 1.6fr', gap: 14 }}><span>Model</span><span>Version</span><span>Served on this instance</span><span>Owner</span><span>Actions</span></div>
          {rows.length === 0 && <div className="empty" data-testid="models-semantic-empty">No semantic model is published to this Gateway. Publish one from a Build project: its Publish tab, “Publish a semantic model”.</div>}
          {rows.map((r) => {
            const served = servedLabel(r)
            const block = serveBlock(r, p.busy)
            return (
              <div key={r.name} data-testid={`semantic-row-${r.name}`}>
                <div className="grid-row auto" style={{ gridTemplateColumns: '1.3fr 0.7fr 3fr 2fr 1.6fr', gap: 14, alignItems: 'start' }}>
                  <span className="mono" data-testid="semantic-name"><Link to={`/models/${encodeURIComponent(r.name)}`} data-testid={`semantic-browse-${r.name}`}>{r.name}</Link></span>
                  <span data-testid="semantic-version">{r.current_version ?? '-'}<div className="hint">{r.versions} kept</div></span>
                  <span style={wrap} data-testid="semantic-served" data-tone={served.tone}>{served.text}</span>
                  <span style={wrap} className="hint">{ownerLabel(r.managed_by)}</span>
                  <span className="row" style={{ gap: 6, flexWrap: 'wrap' }}>
                    <button type="button" className="btn sm" onClick={() => p.onOpen(p.opened === r.name ? null : r.name)} data-testid={`semantic-versions-${r.name}`}>{p.opened === r.name ? 'Hide versions' : 'Versions'}</button>
                    {p.confirming === r.name
                      ? <>
                        <button type="button" className="btn sm btn-primary" disabled={p.busy} onClick={() => p.onRemove(r.name)} data-testid={`semantic-remove-confirm-${r.name}`}>Stop serving {r.name}</button>
                        <button type="button" className="btn sm" onClick={() => p.onAskRemove(null)}>Not now</button>
                      </>
                      : <button type="button" className="btn sm" disabled={p.busy} title={p.busy ? 'Another change is under way.' : undefined} onClick={() => p.onAskRemove(r.name)} data-testid={`semantic-remove-${r.name}`}>Retire</button>}
                  </span>
                </div>
                {p.confirming === r.name && <div className="hint" style={{ padding: '0 14px 8px', ...wrap }} data-testid={`semantic-remove-warning-${r.name}`}>{removeWarning('semantic', r.name)}</div>}
                {p.opened === r.name && (
                  <div style={{ padding: '0 14px' }} data-testid={`semantic-detail-${r.name}`}>
                    <ErrorBanner error={p.detailError} />
                    {!p.detail && !p.detailError && <div className="hint">Loading the versions…</div>}
                    {p.detail && (
                      <>
                        <VersionList versions={p.detail.versions} current={r.current_version} canServe={r.versions > 1} busy={p.busy} onServe={(v) => p.onServe(r.name, v)} prefix="semantic" />
                        {block && r.versions < 2 && <div className="hint" style={{ marginBottom: 8 }} data-testid={`semantic-serve-why-${r.name}`}>{block}</div>}
                        <div className="hint" style={{ marginBottom: 8, ...wrap }} data-testid="semantic-sources">Sources: {Object.keys(p.detail.sources).length === 0 ? 'each reads the data source of its own name' : Object.entries(p.detail.sources).map(([s, d]) => `${s} reads ${d}`).join(', ')}{p.detail.ssas.length > 0 ? `; Analysis Services connection set for ${p.detail.ssas.join(', ')}` : ''}.</div>
                      </>
                    )}
                  </div>
                )}
              </div>
            )
          })}
        </div>
      )}
    </div>
  )
}

export interface EntityModelsViewProps {
  rows: EntityModelRow[] | undefined
  loading: boolean
  listError: unknown
  opened: string | null
  detail: EntityModelDetail | undefined
  detailError: unknown
  confirming: string | null
  busy: boolean
  onOpen: (name: string | null) => void
  onAskRemove: (name: string | null) => void
  onRemove: (name: string) => void
}

/** The entity models a DbDataStore server publishes to the group: where each reads its tables, what this instance serves, its versions, and retire. A new version comes from the Store. */
export function EntityModelsView(p: EntityModelsViewProps) {
  const rows = sortModels(p.rows ?? [])
  return (
    <div data-testid="models-entity-view">
      <ErrorBanner error={p.listError} />
      {p.loading && <div className="empty">Loading…</div>}
      {p.rows && (
        <div className="card flush" data-testid="models-entity-table">
          <div className="grid-head" style={{ gridTemplateColumns: '1.3fr 0.7fr 1.4fr 2.6fr 2fr 1.6fr', gap: 14 }}><span>Model</span><span>Version</span><span>Data source</span><span>Served on this instance</span><span>Entities</span><span>Actions</span></div>
          {rows.length === 0 && <div className="empty" data-testid="models-entity-empty">No entity model is published to this Gateway. A DbDataStore server publishes the models it manages.</div>}
          {rows.map((r) => {
            const served = servedLabel(r)
            return (
              <div key={r.name} data-testid={`entity-row-${r.name}`}>
                <div className="grid-row auto" style={{ gridTemplateColumns: '1.3fr 0.7fr 1.4fr 2.6fr 2fr 1.6fr', gap: 14, alignItems: 'start' }}>
                  <span className="mono" data-testid="entity-name">{r.name}<div className="hint">{ownerLabel(r.managed_by)}</div></span>
                  <span data-testid="entity-version">{r.current_version}</span>
                  <span className="mono" style={wrap}>{r.data_source}</span>
                  <span style={wrap} data-testid="entity-served" data-tone={served.tone}>{served.text}</span>
                  <span style={wrap} className="mono">{r.entities?.join(', ') ?? '-'}</span>
                  <span className="row" style={{ gap: 6, flexWrap: 'wrap' }}>
                    <button type="button" className="btn sm" onClick={() => p.onOpen(p.opened === r.name ? null : r.name)} data-testid={`entity-versions-${r.name}`}>{p.opened === r.name ? 'Hide versions' : 'Versions'}</button>
                    {p.confirming === r.name
                      ? <>
                        <button type="button" className="btn sm btn-primary" disabled={p.busy} onClick={() => p.onRemove(r.name)} data-testid={`entity-remove-confirm-${r.name}`}>Stop serving {r.name}</button>
                        <button type="button" className="btn sm" onClick={() => p.onAskRemove(null)}>Not now</button>
                      </>
                      : <button type="button" className="btn sm" disabled={p.busy} title={p.busy ? 'Another change is under way.' : undefined} onClick={() => p.onAskRemove(r.name)} data-testid={`entity-remove-${r.name}`}>Retire</button>}
                  </span>
                </div>
                {p.confirming === r.name && <div className="hint" style={{ padding: '0 14px 8px', ...wrap }}>{removeWarning('entity', r.name)}</div>}
                {p.opened === r.name && (
                  <div style={{ padding: '0 14px' }} data-testid={`entity-detail-${r.name}`}>
                    <ErrorBanner error={p.detailError} />
                    {!p.detail && !p.detailError && <div className="hint">Loading the versions…</div>}
                    {p.detail && <>
                      <VersionList versions={p.detail.versions} current={r.current_version} canServe={false} busy onServe={() => {}} prefix="entity" />
                      <div className="hint" style={{ marginBottom: 8 }}>An earlier version is served by publishing it again from the Store; this Gateway only retires.</div>
                    </>}
                  </div>
                )}
              </div>
            )
          })}
        </div>
      )}
    </div>
  )
}

/** The models the Gateway serves: semantic models and entity models, with their versions (plan 33 UE12). */
export function ModelsPage() {
  const queryClient = useQueryClient()
  const [opened, setOpened] = useState<string | null>(null)
  const [openedEntity, setOpenedEntity] = useState<string | null>(null)
  const [confirming, setConfirming] = useState<string | null>(null)
  const [confirmingEntity, setConfirmingEntity] = useState<string | null>(null)
  const [notice, setNotice] = useState<{ text: string; bad: boolean } | null>(null)
  const semantic = useQuery({ queryKey: ['gateway', 'models', 'semantic'], queryFn: () => gatewayApi.models.semantic.list(), refetchOnMount: 'always', refetchInterval: 10_000 })
  const entity = useQuery({ queryKey: ['gateway', 'models', 'entity'], queryFn: () => gatewayApi.models.entity.list(), refetchOnMount: 'always', refetchInterval: 10_000 })
  const semanticDetail = useQuery({ queryKey: ['gateway', 'models', 'semantic', opened], queryFn: () => gatewayApi.models.semantic.read(opened!), enabled: opened !== null, refetchOnMount: 'always' })
  const entityDetail = useQuery({ queryKey: ['gateway', 'models', 'entity', openedEntity], queryFn: () => gatewayApi.models.entity.read(openedEntity!), enabled: openedEntity !== null, refetchOnMount: 'always' })
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['gateway', 'models'] })
  const failed = (e: unknown) => setNotice({ text: e instanceof Error ? e.message : 'The Gateway did not answer.', bad: true })
  const serve = useMutation({
    mutationFn: (v: { name: string; version: number }) => gatewayApi.models.semantic.rollback(v.name, v.version),
    onSuccess: (r) => { setNotice({ text: `${r.name} serves version ${r.version} now (it was ${r.from_version}).`, bad: false }); return refresh() },
    onError: failed,
  })
  const retireSemantic = useMutation({
    mutationFn: (name: string) => gatewayApi.models.semantic.remove(name),
    onSuccess: (r) => { setConfirming(null); setOpened(null); setNotice({ text: `${r.name} is not served any more.`, bad: false }); return refresh() },
    onError: (e) => { setConfirming(null); failed(e) },
  })
  const retireEntity = useMutation({
    mutationFn: (name: string) => gatewayApi.models.entity.remove(name),
    onSuccess: (r) => { setConfirmingEntity(null); setOpenedEntity(null); setNotice({ text: `${r.name} is not served any more.`, bad: false }); return refresh() },
    onError: (e) => { setConfirmingEntity(null); failed(e) },
  })
  const busy = serve.isPending || retireSemantic.isPending || retireEntity.isPending
  return (
    <GatewayShell crumbs={[{ label: 'Models', heading: true }]}>
      <div className="pane" data-testid="models-page">
        <Section title="Semantic models" testId="models-semantic" note="Catalogs a Build project (or an administrator) published to this Gateway: what the group serves, which version, and who published it. Moving to an earlier version is instant; every version is kept until the model is retired.">
          <SemanticModelsView
            rows={semantic.data} loading={semantic.isLoading} listError={semantic.error} opened={opened} detail={opened ? semanticDetail.data : undefined} detailError={opened ? semanticDetail.error : null}
            confirming={confirming} busy={busy} notice={notice}
            onOpen={(n) => { setOpened(n); setNotice(null) }} onServe={(name, version) => serve.mutate({ name, version })}
            onAskRemove={(n) => { setConfirming(n); setNotice(null) }} onRemove={(n) => retireSemantic.mutate(n)}
          />
        </Section>
        <Section title="Entity models" testId="models-entity" note="The master data models a DbDataStore server publishes here, so the group serves them with their rules.">
          <EntityModelsView
            rows={entity.data} loading={entity.isLoading} listError={entity.error} opened={openedEntity} detail={openedEntity ? entityDetail.data : undefined} detailError={openedEntity ? entityDetail.error : null}
            confirming={confirmingEntity} busy={busy}
            onOpen={setOpenedEntity} onAskRemove={(n) => { setConfirmingEntity(n); setNotice(null) }} onRemove={(n) => retireEntity.mutate(n)}
          />
        </Section>
      </div>
    </GatewayShell>
  )
}
