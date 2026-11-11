import { Route, Routes } from 'react-router-dom'
import { Navigate } from '@dbdatakit/shell'
import { KeysPage } from './pages/KeysPage'
import { WorkspacePage } from './pages/WorkspacePage'
import { WorkspacesPage } from './pages/WorkspacesPage'

/** The Workspace server's screens as a module of a host (plan 22a, GP31): the projects it hosts, the workspaces people author them in, and one workspace's files, changes, history, commit, push and merge (GP35a), its builds with their preview and publish (GP35b), and the publisher keys. */
export function WorkspaceModule() {
  return (
    <Routes>
      <Route path="/" element={<WorkspacesPage />} />
      <Route path="/keys" element={<KeysPage />} />
      <Route path="/:project/:id" element={<WorkspacePage />} />
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  )
}
