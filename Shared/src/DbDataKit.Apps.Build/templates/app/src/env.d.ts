// `apps build` makes this file from the view files named in app.yaml (the host's loader reads them); it is not in the source folder.
declare module '*.generated.json' {
  import type { ViewsDoc } from '@dbdatakit/view-app'
  const views: ViewsDoc
  export default views
}
