import { Navigate, Route, Routes } from 'react-router-dom'
import { EntityPage } from './pages/EntityPage'
import { ModelsPage } from './pages/ModelsPage'

/** The Store screens as a module of a host (plan 19, Z06): the models, and the rows of each entity. */
export function StoreModule() {
  return (
    <Routes>
      <Route path="/" element={<ModelsPage />} />
      <Route path="/models/:model/:entity" element={<EntityPage />} />
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  )
}
