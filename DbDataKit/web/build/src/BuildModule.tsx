import { Navigate, Route, Routes } from 'react-router-dom'
import { CheckTab, GraphTab, ModelsTab, ProjectPage } from './pages/ProjectPage'
import { CompareTab, PlansTab, SampleTab, StatusTab } from './pages/DeployTabs'
import { EditTab } from './pages/EditTab'
import { ProjectsPage } from './pages/ProjectsPage'
import { PublishTab } from './pages/PublishTab'

/**
 * The Build screens as a module of a host (plan 13, R07): the projects a server hosts, and for each its models, their graph, a check, and where a connection stands, its deploy plans and a sample run. The host has already signed the person in and puts a
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
        <Route path="status" element={<StatusTab />} />
        <Route path="plans" element={<PlansTab />} />
        <Route path="compare" element={<CompareTab />} />
        <Route path="edit" element={<EditTab />} />
        <Route path="publish" element={<PublishTab />} />
        <Route path="sample" element={<SampleTab />} />
      </Route>
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  )
}
