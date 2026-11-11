import { AdminHostProvider } from '@dbdatakit/admin'
import { SyncRoutes } from './App'
import { adminHost } from './adminHost'

/**
 * The Sync screens as a module of a host (plan 13, U08): its routes, and the frame the shared admin screens render in. The host puts a `ModuleProvider` around it that says where it is mounted
 * and which server it talks to, and has already signed the person in.
 */
export function SyncModule() {
  return (
    <AdminHostProvider host={adminHost}>
      <SyncRoutes />
    </AdminHostProvider>
  )
}
