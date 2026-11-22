// @dbdatakit/workspace: the Workspace server's screens, mountable in a host (plan 22a, GP31).
export { WorkspaceModule } from './WorkspaceModule'
export { workspaceNav } from './WorkspaceShell'
export { useSyncEngines, syncEnginesFrom, registryDescriptors, roleWords, SYNC_SOURCE, SYNC_TARGET } from './syncEngines'
export type { EngineRole, RoleStatus, SyncEngine, SyncEngineList, SyncRole } from './syncEngines'
export { registerDesigner, designerOf, designerPath, filesPath } from './designers'
export type { DesignerProps, DesignerRegistration } from './designers'
export { DraftProvider, useDraft, useDraftFile, useDraftFiles, findingsKey } from './draft'
export type { Draft, DraftFile } from './draft'
