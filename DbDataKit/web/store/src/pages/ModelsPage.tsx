import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { ErrorBanner } from '@dbdatakit/shell'
import { storeApi } from '../api'
import { StoreShell } from '../StoreShell'

/** The models this server holds, and the entities of each. */
export function ModelsPage() {
  const models = useQuery({ queryKey: ['store', 'models'], queryFn: storeApi.models })
  return (
    <StoreShell crumbs={[{ label: 'Models', heading: true }]}>
      <div className="pane" data-testid="store-models">
        <div className="page-head"><h1 className="page-title">Models</h1></div>
        <ErrorBanner error={models.error} />
        {models.data?.length === 0 && <div className="empty" data-testid="store-models-empty">This server holds no models yet.</div>}
        {(models.data ?? []).map((m) => (
          <div key={m.name} className="card" data-testid="store-model">
            <div className="card-body">
              <div className="card-title mono">{m.title ?? m.name}{m.valid ? '' : ' (not valid)'}</div>
              <div className="row" style={{ gap: 12, flexWrap: 'wrap' }}>
                {m.entities.map((e) => <Link key={e} to={`/models/${encodeURIComponent(m.name)}/${encodeURIComponent(e)}`}>{e}</Link>)}
              </div>
            </div>
          </div>
        ))}
      </div>
    </StoreShell>
  )
}
