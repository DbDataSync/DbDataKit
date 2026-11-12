import { useQuery } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'
import { GraphView } from '@dbdatakit/build/graph'
import { workspaceApi } from '../api'
import { graphOfLoads, type LoadGraph } from '../buildGraph'
import { planFilesOf } from '../buildPlan'
import { BuildLineagePanel } from './BuildLineagePanel'

/** The load graph without its query, so a test can draw it: the picture Build draws (`GraphView`), over the graph read from the draft's compiled plan, and a sentence for whatever could not be shown. */
export function LoadGraphView({ graph, connections }: { graph: LoadGraph; connections: number }) {
  return (
    <div data-testid="wsp-build-graph">
      {connections === 0 && <div className="empty" data-testid="wsp-build-graph-none">Nothing is compiled yet, so there is no graph to draw. <code>dbdatabuild project compile</code> writes <code>rendered/&lt;connection&gt;/loads.yml</code>; the graph is read from it.</div>}
      {graph.unreadable.map((u) => <div key={u.connection} className="banner error" role="alert" data-testid="wsp-build-graph-unreadable"><span className="mark">!</span><span><code>rendered/{u.connection}/loads.yml</code>: {u.why}</span></div>)}
      {graph.withoutGraph.length > 0 && <div className="hint" data-testid="wsp-build-graph-old">{graph.withoutGraph.map((c) => `rendered/${c}/loads.yml`).join(', ')} was compiled before the dependency graph was written, so its models are shown without what they are built from. Compile again to draw the edges.</div>}
      {graph.nodes.length > 0 && <GraphView nodes={graph.nodes} edges={graph.edges} />}
      {graph.nodes.length > 0 && <div className="hint" style={{ marginTop: 6 }} data-testid="wsp-build-graph-count">{graph.nodes.length} {graph.nodes.length === 1 ? 'model' : 'models'}, {graph.edges.length} {graph.edges.length === 1 ? 'edge' : 'edges'} (the compiled plan of the draft's last commit or save, not the models as typed since). Choose a model to light up what it is built from and what is built from it.</div>}
    </div>
  )
}

/** The load graph of a Build draft, read from the committed `rendered/<connection>/loads.yml` of the draft itself through the Workspace's file route: not from Build's server. */
export function BuildGraphPanel({ project, id, paths }: { project: string; id: string; paths: readonly string[] }) {
  const loads = planFilesOf(paths).filter((c) => c.loads !== null)
  const files = loads.map((c) => ({ connection: c.connection, path: c.loads! }))
  const texts = useQuery({
    queryKey: ['workspace', 'graph-texts', project, id, files.map((f) => f.path)],
    queryFn: async () => Promise.all(files.map(async (f) => ({ connection: f.connection, text: await workspaceApi.readFile(project, id, f.path) }))),
    enabled: files.length > 0,
  })
  return (
    <>
      <ErrorBanner error={texts.error} />
      <LoadGraphView graph={graphOfLoads(texts.data ?? [])} connections={files.length} />
      <BuildLineagePanel project={project} id={id} />
    </>
  )
}
