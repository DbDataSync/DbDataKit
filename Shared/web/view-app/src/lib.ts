// @dbdatakit/view-app as a library: what an app's own code builds on (the template, plan 22a GP32). The host's runtime for an app of views only is `main.tsx`; this file is what an app imports.
export { ROW_CAP, appFetcher, appSemanticClient, appViewsClient, appStoreClient, connectHost, loadAll, type Host, type Loaded } from './data'

// the components that draw view-spec/1 files, and the pages of code that sit beside them (plan 22a GP34)
export { ViewApp, menuOf, type CodePage, type CodePages, type PageProps, type ViewAppProps } from './screens'
export type { ViewsDoc, ViewSpec } from './spec'
