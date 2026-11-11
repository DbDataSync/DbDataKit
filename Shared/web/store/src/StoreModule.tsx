import { Route, Routes } from 'react-router-dom'
import { Navigate } from '@dbdatakit/shell'
import { EntityPage } from './pages/EntityPage'
import { ModelWorkspace } from './pages/ModelWorkspace'
import { ModelsPage } from './pages/ModelsPage'
import { NewModelPage } from './pages/NewModelPage'

/** The Store screens as a module of a host (plan 19, Z06; plan 33, UE13 and UE14): the models, one model's definition, storage and change requests, and the rows of each entity. */
export function StoreModule() {
  return (
    <Routes>
      <Route path="/" element={<ModelsPage />} />
      <Route path="/new-model" element={<NewModelPage />} />
      <Route path="/model/:model/*" element={<ModelWorkspace />} />
      <Route path="/models/:model/:entity" element={<EntityPage />} />
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  )
}
