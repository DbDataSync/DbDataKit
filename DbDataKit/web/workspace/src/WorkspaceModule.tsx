import { Route, Routes } from 'react-router-dom'
import { Navigate } from '@dbdatakit/shell'
import { WorkspacesPage } from './pages/WorkspacesPage'

/** The Workspace server's screens as a module of a host (plan 22a, GP31): the projects it hosts and the workspaces people author them in. */
export function WorkspaceModule() {
  return (
    <Routes>
      <Route path="/" element={<WorkspacesPage />} />
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  )
}
