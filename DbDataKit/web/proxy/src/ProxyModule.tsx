import { Navigate, Route, Routes, useLocation, useParams } from 'react-router-dom'
import { ActivityPage } from './pages/ActivityPage'
import { ConfigCommitPage, ConfigHistoryPage } from './pages/ConfigPage'
import { DataSourceEditPage, DataSourcesPage } from './pages/DataSourcesPage'
import { IdentitiesPage } from './pages/IdentitiesPage'
import { OverviewPage } from './pages/OverviewPage'
import { TlsPage } from './pages/TlsPage'

function EditRoute() {
  const { name = '' } = useParams()
  const isNew = new URLSearchParams(useLocation().search).get('new') === '1'
  return <DataSourceEditPage key={name} name={name} isNew={isNew} />
}

/** The Proxy screens as a module of a host (plan 13, P08/P09): overview, data sources, users and sign-in, certificate, activity and the history of the configuration. */
export function ProxyModule() {
  return (
    <Routes>
      <Route path="/" element={<OverviewPage />} />
      <Route path="/data-sources" element={<DataSourcesPage />} />
      <Route path="/data-sources/:name" element={<EditRoute />} />
      <Route path="/identities" element={<IdentitiesPage />} />
      <Route path="/tls" element={<TlsPage />} />
      <Route path="/activity" element={<ActivityPage />} />
      <Route path="/config" element={<ConfigHistoryPage />} />
      <Route path="/config/:sha" element={<ConfigCommitPage />} />
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  )
}
