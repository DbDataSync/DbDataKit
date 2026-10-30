import { Route, Routes } from 'react-router-dom'
import { InvitePage, UsersPage } from '@dbdatakit/admin'
import { SignInScreen, useAuthStatus } from '@dbdatakit/shell'
import { PRODUCT } from './ConsoleShell'
import { DocsDirectoryPage, ModuleDocsPage } from './pages/DocsPages'
import { HomePage } from './pages/HomePage'
import { InstancesPage } from './pages/InstancesPage'
import { MonitoringPage } from './pages/MonitoringPage'
import { ServerModule } from './pages/ServerModule'
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
      <Route path="/servers/:instance/*" element={<ServerModule />} />
      <Route path="/docs" element={<DocsDirectoryPage />} />
      <Route path="/docs/:module/*" element={<ModuleDocsPage />} />
      <Route path="/views" element={<ViewsPage />} />
      <Route path="/monitoring" element={<MonitoringPage />} />
      <Route path="/users" element={<UsersPage />} />
      <Route path="*" element={<HomePage />} />
    </Routes>
  )
}
