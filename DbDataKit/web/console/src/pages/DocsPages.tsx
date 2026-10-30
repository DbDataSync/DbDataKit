import { useMemo } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link, useParams } from 'react-router-dom'
import { bundledDocs, DocsViewer, ErrorBanner } from '@dbdatakit/shell'
import { ConsoleShell } from '../ConsoleShell'

/** A module whose docs this console was built with (the build's `docs/index.json`: every package that registers docs in its package.json). */
export interface DocsModuleEntry {
  id: string
  title: string
}

async function listModules(): Promise<DocsModuleEntry[]> {
  const response = await fetch('/docs/index.json', { credentials: 'same-origin' })
  if (!response.ok || !response.headers.get('content-type')?.toLowerCase().startsWith('application/json')) throw new Error('This console has no docs in its build.')
  return (await response.json()) as DocsModuleEntry[]
}

const modulesKey = ['docs', 'modules'] as const

/** `/docs`: every module's docs, one entry each (plan 13a, U11). A module registers its docs with the build and appears here with no change to this page. */
export function DocsDirectoryPage() {
  const modules = useQuery({ queryKey: modulesKey, queryFn: listModules, staleTime: Infinity, retry: false })
  return (
    <ConsoleShell crumbs={[{ label: 'Docs', heading: true }]}>
      <div className="pane" data-testid="docs-directory">
        <div className="page-head">
          <h1 className="page-title">Documentation</h1>
          <span className="page-note">The docs of every product this console has screens for, as they were when this console was built.</span>
        </div>
        <ErrorBanner error={modules.error} />
        {modules.data?.length === 0 && <div className="empty">No product has docs in this build.</div>}
        <ul className="docs-index-list" data-testid="docs-modules">
          {(modules.data ?? []).map((m) => <li key={m.id}><Link to={`/docs/${m.id}`} data-testid={`docs-module-${m.id}`}>{m.title}</Link></li>)}
        </ul>
      </div>
    </ConsoleShell>
  )
}

/** `/docs/<module>` and `/docs/<module>/<page>`: one module's docs in the console's own frame, by the same viewer a module shows inside its server's screens. */
export function ModuleDocsPage() {
  const { module = '', '*': rest = '' } = useParams()
  const page = rest.split('/')[0] || undefined
  const modules = useQuery({ queryKey: modulesKey, queryFn: listModules, staleTime: Infinity, retry: false })
  const entry = modules.data?.find((m) => m.id === module)
  const source = useMemo(() => (entry ? bundledDocs({ id: entry.id, title: entry.title }) : undefined), [entry])
  return (
    <ConsoleShell crumbs={[{ label: 'Docs', to: '/docs' }, { label: entry?.title ?? module, to: page ? `/docs/${module}` : undefined }]}>
      {source
        ? <DocsViewer source={source} page={page} base={`/docs/${module}`} versionNote="For the version of this console" />
        : modules.isLoading
          ? null
          : <div className="pane"><div className="empty" data-testid="docs-module-unknown">This console has no docs for {module}.</div></div>}
    </ConsoleShell>
  )
}
