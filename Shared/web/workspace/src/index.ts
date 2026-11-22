// @dbdatakit/workspace: the Workspace server's screens, mountable in a host (plan 22a, GP31).
export { WorkspaceModule } from './WorkspaceModule'
export { workspaceNav } from './WorkspaceShell'
export { useSyncEngines, syncEnginesFrom, registryDescriptors, roleWords, SYNC_SOURCE, SYNC_TARGET } from './syncEngines'
export type { EngineRole, RoleStatus, SyncEngine, SyncEngineList, SyncRole } from './syncEngines'
