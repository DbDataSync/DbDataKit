// What the Workspace's Build editors take from the standalone Build screens (plan 33 SC20m): the pure helpers and the two connection editors, imported so the two never describe a model or a connection differently.
export { MODEL_KINDS, modelNameProblem } from './pages/NewModelPanel'
export { CONNECTION_ENGINES, connectionPath, connectionProblem, connectionYaml, emptyConnection, type ConnectionForm } from './pages/NewConnectionPanel'
export { ConnectionGrantsEditor } from './pages/ConnectionGrants'
export { ConnectionPolicyEditor } from './pages/ConnectionPolicy'
// the dashboards and reports of a project (plan 33 SC20n): where they live, how a file is read as one, and the `ViewsClient` over a draft's files
export { FOLDERS, FileRefusal, semanticModelName, viewFileOf, viewFiles, workspaceViewsClient, type FilesApi } from './views/workspaceStore'
