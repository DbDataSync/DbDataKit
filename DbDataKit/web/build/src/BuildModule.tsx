import { Navigate, Route, Routes } from 'react-router-dom'
import { CheckTab, GraphTab, ModelsTab, ProjectPage } from './pages/ProjectPage'
import { ProjectsPage } from './pages/ProjectsPage'

/**
 * The Build screens as a module of a host (plan 13, R07): the projects a server hosts, and for each its models, their graph and a check. The host has already signed the person in and puts a
 * `ModuleProvider` around this that says where it is mounted and which server it talks to.
 */
export function BuildModule() {
  return (
    <Routes>
      <Route path="/" element={<ProjectsPage />} />
      <Route path="/projects/:project" element={<ProjectPage />}>
        <Route index element={<ModelsTab />} />
        <Route path="graph" element={<GraphTab />} />
        <Route path="check" element={<CheckTab />} />
      </Route>
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  )
}
