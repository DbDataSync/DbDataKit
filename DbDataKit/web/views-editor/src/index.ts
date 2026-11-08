/**
 * The screens of the dashboard and report editor (plan 32 DBR24 to DBR27, moved here by DBR29): the console's Views page and Build's Views tab are their two hosts. The editor's logic (draft, commands, geometry, stores) is `@dbdatakit/visuals`;
 * what is here draws it and takes everything that differs between hosts through `EditorHost`.
 */
export { DashboardEditor, type EditorHost } from './DashboardEditor'
export { newWidget, nextTile, modelOf } from './editing'
