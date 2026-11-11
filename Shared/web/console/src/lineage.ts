import type { LineageGraphDoc, LineageNodeDoc } from '@dbdatakit/visuals'
import type { LineageBlock } from './api'

/** Nodes that are drawn as cards: every node that is not a column. */
export const isCard = (node: LineageNodeDoc) => node.kind !== 'column'

/** The cards a search finds: those whose name or id holds every word typed, case ignored, shortest name first. */
export function findCards(graph: LineageGraphDoc, text: string, limit = 12): LineageNodeDoc[] {
  const words = text.toLowerCase().split(/\s+/).filter((w) => w !== '')
  if (words.length === 0) return []
  return graph.nodes
    .filter(isCard)
    .filter((n) => words.every((w) => n.name.toLowerCase().includes(w) || n.id.toLowerCase().includes(w)))
    .sort((a, b) => a.name.length - b.name.length || a.id.localeCompare(b.id))
    .slice(0, limit)
}

/** The sentence under a server's name in the list of what answered. */
export function serverNote(s: { nodes: number | null; edges: number | null; error: string | null; problems: string[] }): string {
  if (s.error) return `did not give its lineage: ${s.error}`
  const size = `${s.nodes ?? 0} nodes, ${s.edges ?? 0} edges`
  return s.problems.length > 0 ? `${size}; ${s.problems.length} left out` : size
}

/** The graph without the deployment nodes (packages, versions, channels, runners, their runs): those have their own screen, Deployments, and are not a flow of data. */
export function withoutDeployments<T extends LineageGraphDoc>(graph: T): T {
  const drop = new Set(graph.nodes.filter((n) => n.kind === 'deployment' || n.properties?.deployment !== undefined).map((n) => n.id))
  return { ...graph, nodes: graph.nodes.filter((n) => !drop.has(n.id)), edges: graph.edges.filter((e) => !drop.has(e.from) && !drop.has(e.to)) }
}

/** The status a node's checks give it (plan 23a FQ16), or null when it has none: `blocked`, `fail`, `error` or `pass`. */
export function checkStatusOf(node: LineageNodeDoc): 'blocked' | 'fail' | 'error' | 'pass' | null {
  const status = node.properties?.check_status
  return status === 'blocked' || status === 'fail' || status === 'error' || status === 'pass' ? status : null
}

/** The sentence about a node's checks: what they say, and for a block what it stops. Null when the node has no check. */
export function checkLine(node: LineageNodeDoc, blocks: LineageBlock[] | undefined): string | null {
  const status = checkStatusOf(node)
  if (status === null) return null
  const count = Number(node.properties?.check_count ?? 0)
  const failing = Number(node.properties?.check_failing ?? 0)
  const of = `${count} check${count === 1 ? '' : 's'}`
  if (status === 'pass') return `${of}, all passing`
  if (status === 'error') return `${of}, ${failing} could not run`
  if (status === 'fail') return `${of}, ${failing} failing (warn only: nothing is stopped)`
  const stops = blocks?.find((b) => b.node === node.id)?.stops ?? []
  const names = stops.map((s) => s.name)
  return `${of}, ${failing} failing, blocked by ${(node.properties?.check_blocking_ids ?? '').split(',').join(', ')}; ` +
    (stops.length === 0 ? 'nothing depends on it' : `stops ${stops.length}: ${names.slice(0, 5).join(', ')}${names.length > 5 ? ', ...' : ''}`)
}
