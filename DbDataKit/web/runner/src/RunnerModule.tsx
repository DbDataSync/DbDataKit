import { Navigate, Route, Routes } from 'react-router-dom'
import { PackagesPage } from './pages/PackagesPage'
import { RunsPage } from './pages/RunsPage'
import { TasksPage } from './pages/TasksPage'

/** The runner's screens as a module of a host (plan 28a, R19): the group's instances and the tasks, the runs, and the packages with their staging and activation. */
export function RunnerModule() {
  return (
    <Routes>
      <Route path="/" element={<TasksPage />} />
      <Route path="/runs" element={<RunsPage />} />
      <Route path="/packages" element={<PackagesPage />} />
      <Route path="/packages/:name" element={<PackagesPage />} />
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  )
}
