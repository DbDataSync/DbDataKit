import { bundledDocs } from '@dbdatakit/shell'

/**
 * DbDataSync's docs as a module registers them (plan 13a, U11): the files of `DbDataSync/docs`, in the order of its `order.txt`, with the pictures they show, generated into the console's build under
 * `/docs/sync` (see `docs` in this package's `package.json`, and `console/build/moduleDocs.mjs`). They are not committed twice: the folder is the only copy.
 */
export const syncDocs = bundledDocs({ id: 'sync', title: 'DbDataSync' })
