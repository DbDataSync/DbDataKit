import { useQuery } from '@tanstack/react-query'
import { ErrorBanner, Link, useIsAdmin } from '@dbdatakit/shell'
import { storeApi } from '../api'
import { StoreShell } from '../StoreShell'

/** The models this server holds, the entities of each, and the way into a model's definition, storage and change requests. */
export function ModelsPage() {
  const models = useQuery({ queryKey: ['store', 'models'], queryFn: storeApi.models })
  const canWrite = useIsAdmin()
  return (
    <StoreShell crumbs={[{ label: 'Models', heading: true }]}>
      <div className="pane" data-testid="store-models">
        <div className="page-head">
          <h1 className="page-title">Models</h1>
          <div className="row" style={{ gap: 10, alignItems: 'center' }}>
            {canWrite
              ? <Link to="/new-model" className="btn btn-primary" data-testid="store-model-new">New model</Link>
              : <span className="hint" data-testid="store-model-new-why">Making a model needs the author or administrator role.</span>}
          </div>
        </div>
        <ErrorBanner error={models.error} />
        {models.isLoading && <div className="empty" data-testid="store-models-loading">Loading...</div>}
        {models.data?.length === 0 && <div className="empty" data-testid="store-models-empty">This server holds no models yet.{canWrite ? ' Make one with New model.' : ''}</div>}
        {(models.data ?? []).map((m) => (
          <div key={m.name} className="card" data-testid="store-model" data-model={m.name}>
            <div className="card-body">
              <div className="card-title mono" data-testid="store-model-title">{m.title ?? m.name}{m.valid ? '' : ' (not valid)'}</div>
              <div className="row" style={{ gap: 12, flexWrap: 'wrap' }}>
                {m.entities.map((e) => <Link key={e} to={`/models/${encodeURIComponent(m.name)}/${encodeURIComponent(e)}`} data-testid="store-entity-link">{e}</Link>)}
              </div>
              <div className="row" style={{ gap: 12, flexWrap: 'wrap', marginTop: 8 }}>
                <Link to={`/model/${encodeURIComponent(m.name)}`} data-testid="store-model-definition">Definition</Link>
                <Link to={`/model/${encodeURIComponent(m.name)}/storage`} data-testid="store-model-storage">Storage</Link>
              </div>
            </div>
          </div>
        ))}
      </div>
    </StoreShell>
  )
}
