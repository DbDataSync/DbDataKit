// @dbdatakit/admin: the host-administration screens the console shows (users and invitations).
// A product renders them inside its own frame through AdminHostProvider. Imports nothing from any product (a test checks).
export { AdminHostProvider, useAdminHost } from './host'
export type { AdminFrame, AdminHost } from './host'

export { InvitePage } from './pages/InvitePage'
export { UsersPage } from './pages/UsersPage'
