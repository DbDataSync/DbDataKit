import { useMemo } from 'react'
import { useNavigate } from 'react-router-dom'
import { useQueryClient } from '@tanstack/react-query'
import { clientStore, type ViewKind } from '@dbdatakit/visuals'
import { DashboardEditor as Editor, type EditorHost } from '@dbdatakit/views-editor'
import { viewsClient, FIXTURES } from '../viewsSource'
import { withSource } from '../viewsUrl'

const AREA: Record<ViewKind, string> = { dashboard: '/views/dashboards', report: '/views/reports' }

/**
 * The editor of the Views page (plan 32 DBR24): the shared screen (`@dbdatakit/views-editor`, which Build's Views tab hosts as well) over one source of the console, with the console's own addresses: the list, the page that draws
 * the document and the editor again (each keeping the `?source=` the person came with).
 */
export function DashboardEditor({ kind, name, source = FIXTURES, keep = null }: { kind: ViewKind; name: string | null; source?: string; keep?: string | null }) {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const host = useMemo<EditorHost>(() => {
    const client = viewsClient(source)
    const area = (path: string) => withSource(path, keep)
    return {
      client,
      store: clientStore(client),
      listHref: (k) => area(AREA[k]),
      viewHref: (k, n) => area(`${AREA[k]}/${encodeURIComponent(n)}`),
      editHref: (k, n) => area(`${AREA[k]}/${encodeURIComponent(n)}?edit=1`),
      go: (href, options) => navigate(href, options),
      ...(source === FIXTURES ? {} : { deleteQuestion: (_k: ViewKind, n: string) => `Stop serving ${n} to the group? Its versions are kept and publishing it again brings it back (Versions on the board).` }),
      changed: () => queryClient.invalidateQueries({ queryKey: ['views'] }),
    }
  }, [source, keep, navigate, queryClient])
  return <Editor kind={kind} name={name} host={host} />
}
