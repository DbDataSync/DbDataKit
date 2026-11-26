import { Route, Routes } from 'react-router-dom'
import { Navigate } from '@dbdatakit/shell'
import { WorkspacePage } from './pages/WorkspacePage'
import { WorkspacesPage } from './pages/WorkspacesPage'

/** The Workspace server's screens as a module of a host: the projects it hosts, the workspaces people author them in, one draft in its two areas (Files, and the Designer a host registered for its kind with `registerDesigner`: `/:project/:draft/files/...`, `/:project/:draft/designer/...`) with its builds. */
export function WorkspaceModule() {
  return (
    <Routes>
      <Route path="/" element={<WorkspacesPage />} />
      <Route path="/:project/:id/*" element={<WorkspacePage />} />
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  )
}
