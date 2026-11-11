import { Route, Routes } from 'react-router-dom'
import { InvitePage, UsersPage } from '@dbdatakit/admin'
import { SignInScreen, useAuthStatus } from '@dbdatakit/shell'
import { PRODUCT } from './ConsoleShell'
import { ConnectionsPage } from './pages/ConnectionsPage'
import { DocsDirectoryPage, ModuleDocsPage } from './pages/DocsPages'
import { HomePage } from './pages/HomePage'
import { InstancesPage } from './pages/InstancesPage'
import { UpgradesPage } from './pages/UpgradesPage'
import { MonitoringPage } from './pages/MonitoringPage'
import { ApprovalsPage } from './pages/ApprovalsPage'
import { RunsPage } from './pages/RunsPage'
import { AuditPage } from './pages/AuditPage'
import { ServerModule } from './pages/ServerModule'
import { ServerStatePage } from './pages/ServerStatePage'
import { LineagePage } from './pages/LineagePage'
import { QualityPage } from './pages/QualityPage'
import { DeploymentsPage } from './pages/DeploymentsPage'
import { ViewsPage } from './pages/ViewsPage'

export default function App() {
  const { data: status, isLoading } = useAuthStatus()

  // Nothing is rendered until it is known whether anybody is signed in. Rendering the app first and
  // correcting to a sign-in screen would flash a shell full of failed requests.
  if (isLoading) return null

  // Redemption is reachable without a session — that is the entire point of an invitation, and
  // routing it behind the sign-in screen would be a door locked from the inside.
  if (window.location.pathname === '/invite') return <InvitePage product={PRODUCT} />

  if (status && !status.authenticated) return <SignInScreen product={PRODUCT} />

  return (
    <Routes>
      <Route path="/" element={<HomePage />} />
      <Route path="/instances" element={<InstancesPage />} />
      <Route path="/upgrades" element={<UpgradesPage />} />
      <Route path="/state/:instance" element={<ServerStatePage />} />
      <Route path="/servers/:instance/*" element={<ServerModule />} />
      <Route path="/connections" element={<ConnectionsPage />} />
      <Route path="/docs" element={<DocsDirectoryPage />} />
      <Route path="/docs/:module/*" element={<ModuleDocsPage />} />
      <Route path="/lineage" element={<LineagePage />} />
      <Route path="/quality" element={<QualityPage />} />
      <Route path="/deployments" element={<DeploymentsPage />} />
      <Route path="/views" element={<ViewsPage />} />
      <Route path="/views/dashboards" element={<ViewsPage area="dashboards" />} />
      <Route path="/views/dashboards/:name" element={<ViewsPage area="dashboards" />} />
      <Route path="/views/reports" element={<ViewsPage area="reports" />} />
      <Route path="/views/reports/:name" element={<ViewsPage area="reports" />} />
      <Route path="/monitoring" element={<MonitoringPage />} />
      <Route path="/approvals" element={<ApprovalsPage />} />
      <Route path="/runs" element={<RunsPage />} />
      <Route path="/audit" element={<AuditPage />} />
      <Route path="/users" element={<UsersPage />} />
      <Route path="*" element={<HomePage />} />
    </Routes>
  )
}
