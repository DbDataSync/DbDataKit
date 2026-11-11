import type { CatalogAttribute, CatalogMeasure, SemanticCatalogView } from './types'

/** The console's Lineage page, searching for the node Build's lineage makes for a member of a model (`<model>: <member>`, a `SemanticMember`): a measure or an attribute is followed back to the Build column that feeds it. */
export const lineageLink = (model: string, member: string): string => `/lineage?find=${encodeURIComponent(`${model}: ${member}`)}`

/** The sentence a measure reads as: its aggregation and column, or the operation it derives with. */
export function measureLine(m: CatalogMeasure): string {
  if (m.derived) return `derived: ${m.derived} of other measures`
  const of = m.column ? `${m.aggregation ?? 'sum'} of ${m.column}` : (m.aggregation ?? '')
  return m.source ? `${of} (source ${m.source})` : of
}

/** Where an attribute is read from, one phrase per source it is bound in. */
export const bindingLine = (a: CatalogAttribute): string =>
  a.bindings.length === 0 ? 'not bound to a column' : a.bindings.map((b) => `${b.source}: ${b.join ? `${b.join}.` : ''}${b.column}`).join('; ')

/** The dimensions and measures whose name, label or synonyms hold every word typed. */
export function filterCatalog(view: SemanticCatalogView, text: string): { dimensions: SemanticCatalogView['dimensions']; measures: SemanticCatalogView['measures'] } {
  const words = text.toLowerCase().split(/\s+/).filter((w) => w !== '')
  if (words.length === 0) return { dimensions: view.dimensions, measures: view.measures }
  const hit = (name: string, synonyms: string[], label?: string | null) => {
    const hay = [name, label ?? '', ...synonyms].join(' ').toLowerCase()
    return words.every((w) => hay.includes(w))
  }
  const dimensions = view.dimensions
    .map((d) => ({ ...d, attributes: d.attributes.filter((a) => hit(a.name, a.synonyms)) }))
    .filter((d) => d.attributes.length > 0)
  return { dimensions, measures: view.measures.filter((m) => hit(m.name, m.synonyms)) }
}
