import { Navigate, Route, Routes, useLocation, useParams } from 'react-router-dom'
import { ActivityPage } from './pages/ActivityPage'
import { AppsPage } from './pages/AppsPage'
import { CachePage } from './pages/CachePage'
import { ConfigCommitPage, ConfigHistoryPage } from './pages/ConfigPage'
import { DataSourceEditPage, DataSourcesPage } from './pages/DataSourcesPage'
import { IdentitiesPage } from './pages/IdentitiesPage'
import { IdentityAdminPage } from './identity/IdentityAdminPage'
import { GatewayShell } from './GatewayShell'
import { ExplorerPage } from './pages/ExplorerPage'
import { ModelBrowserPage } from './pages/ModelBrowserPage'
import { ModelsPage } from './pages/ModelsPage'
import { OverviewPage } from './pages/OverviewPage'
import { SavedQueriesPage } from './pages/SavedQueriesPage'
import { SecretsPage } from './pages/SecretsPage'
import { SettingsPage } from './pages/SettingsPage'
import { TlsPage } from './pages/TlsPage'

function EditRoute() {
  const { name = '' } = useParams()
  const isNew = new URLSearchParams(useLocation().search).get('new') === '1'
  return <DataSourceEditPage key={name} name={name} isNew={isNew} />
}

/** The Gateway screens as a module of a host (plan 13, P08/P09): overview, data sources, users and sign-in, the identity home it serves, the apps it publishes (AP24), certificate, activity and the history of the configuration. */
export function GatewayModule() {
  return (
    <Routes>
      <Route path="/" element={<OverviewPage />} />
      <Route path="/data-sources" element={<DataSourcesPage />} />
      <Route path="/data-sources/:name" element={<EditRoute />} />
      <Route path="/identities" element={<IdentitiesPage />} />
      <Route path="/identity-home" element={<IdentityAdminPage Frame={GatewayShell} />} />
      <Route path="/secrets" element={<SecretsPage />} />
      <Route path="/apps" element={<AppsPage />} />
      <Route path="/models" element={<ModelsPage />} />
      <Route path="/models/:name" element={<ModelBrowserPage />} />
      <Route path="/models/:name/explore" element={<ExplorerPage />} />
      <Route path="/saved-queries" element={<SavedQueriesPage />} />
      <Route path="/cache" element={<CachePage />} />
      <Route path="/tls" element={<TlsPage />} />
      <Route path="/activity" element={<ActivityPage />} />
      <Route path="/settings" element={<SettingsPage />} />
      <Route path="/config" element={<ConfigHistoryPage />} />
      <Route path="/config/:sha" element={<ConfigCommitPage />} />
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  )
}
