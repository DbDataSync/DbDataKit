import type { CodePages } from '@dbdatakit/view-app'

// Pages written as code, shown in the menu beside the view files. None to begin with: an app can be only spec files.
//
// To make one page code, write a component in this folder and register it under its route:
//
//   // src/pages/Overview.tsx
//   import type { PageProps } from '@dbdatakit/view-app'
//   export function Overview({ client, params, go }: PageProps) { ... }   // client reads and writes the Store; go('/views/territories.yaml') moves to a view
//
//   // here
//   import { Overview } from './Overview'
//   export const pages: CodePages = { 'pages/overview': { title: 'Overview', component: Overview } }
//
// A page whose key is also a view file of app.yaml (for example 'views/dashboard.yaml') is drawn instead of that file; to drop a view for good, delete the file and its line in `views:`.
export const pages: CodePages = {}
