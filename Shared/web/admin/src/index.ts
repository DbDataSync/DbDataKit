// @dbdatakit/admin: the host-administration screens every product's console shares (configuration, certificate, updates, drivers, libraries, files, docs, users and invitations).
// A product renders them inside its own frame through AdminHostProvider. Imports nothing from any product (a test checks).
export { AdminHostProvider, useAdminHost } from './host'
export type { AdminCodeEditor, AdminFrame, AdminHost } from './host'

export { adminApi } from './api/adminApi'
export * from './api/hooks'
export type * from './api/types'

export { AdminConfigPage } from './pages/AdminConfigPage'
export { AdminCertificatePage } from './pages/AdminCertificatePage'
export { AdminUpdatesPage } from './pages/AdminUpdatesPage'
export { DocsPage } from './pages/DocsPage'
export { DriverEditPage } from './pages/DriverEditPage'
export { DriversPage } from './pages/DriversPage'
export { FilesPage } from './pages/FilesPage'
export { LibrariesPage } from './pages/LibrariesPage'
export { InvitePage } from './pages/InvitePage'
export { UsersPage } from './pages/UsersPage'

export { AdminTabs } from './components/AdminTabs'
export { DriversTabs } from './components/DriversTabs'
export { RestartRequiredBanner } from './components/RestartRequiredBanner'
export { FileUploadPanel } from './components/FileUploadPanel'
export { LibraryFindPanel } from './components/LibraryFindPanel'
