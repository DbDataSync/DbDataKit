import type { CatalogRoles } from './derive'
import { roleOf } from './derive'
import type { ViewsDoc } from './model'

/**
 * The catalog of a semantic model as the editor's field wells see it (plan 32 DBR25): the names of its measures and attributes, with the words a person reads beside them. The Proxy's
 * `GET /api/v1/semantic-models/{name}` and Build's `project compile` both give it; `catalogFromModel` reads either shape loosely (nothing here validates a model: the engine does, at publish), and `catalogRoles`
 * turns it into what `deriveRequest` needs.
 */
export interface CatalogMember { name: string; label?: string; description?: string }
export interface Catalog { model: string; measures: CatalogMember[]; attributes: CatalogMember[] }

export const catalogRoles = (catalog: Catalog): CatalogRoles => ({ model: catalog.model, measures: catalog.measures.map((m) => m.name), attributes: catalog.attributes.map((m) => m.name) })

const isObject = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v)
const text = (v: unknown): string | undefined => (typeof v === 'string' && v.length > 0 ? v : undefined)

function members(node: unknown, prefix = ''): CatalogMember[] {
  const found: CatalogMember[] = []
  const add = (name: string, body: unknown) => {
    const b = isObject(body) ? body : {}
    found.push({ name: prefix && !name.includes('.') ? `${prefix}.${name}` : name, label: text(b.label) ?? text((b.synonyms as unknown[] | undefined)?.[0]), description: text(b.description) })
  }
  if (Array.isArray(node)) for (const n of node) { if (typeof n === 'string') add(n, {}); else if (isObject(n) && typeof n.name === 'string') add(n.name, n) }
  else if (isObject(node)) for (const [name, body] of Object.entries(node)) add(name, body)
  return found
}

/** A model's catalog from its JSON: `measures` as a map or a list, and `dimensions` (a map or a list of `{name, attributes}`, an attribute `region` of `customers` named `customers.region`). Null when it holds neither. */
export function catalogFromModel(model: unknown): Catalog | null {
  if (!isObject(model)) return null
  const measures = members(model.measures)
  const attributes: CatalogMember[] = []
  const dimensions = model.dimensions
  if (Array.isArray(dimensions)) for (const d of dimensions) { if (isObject(d) && typeof d.name === 'string') attributes.push(...members(d.attributes, d.name)) }
  else if (isObject(dimensions)) for (const [name, d] of Object.entries(dimensions)) if (isObject(d)) attributes.push(...members(d.attributes, name))
  if (measures.length === 0 && attributes.length === 0) return null
  return { model: text(model.name) ?? '', measures, attributes }
}

/** The retail sample's catalog (`DbDataBuild/templates/retail/semantic/sales.yml`): the fixture source's model and the tests'. */
export const retailCatalog: Catalog = {
  model: 'retail_sales',
  measures: [
    { name: 'sales.net_amount', label: 'Net sales' },
    { name: 'sales.gross_amount', label: 'Gross sales' },
    { name: 'sales.margin_amount', label: 'Margin' },
    { name: 'sales.units', label: 'Units' },
    { name: 'sales.order_count', label: 'Orders' },
    { name: 'returns.refund_amount', label: 'Refunds' },
    { name: 'sales.margin_rate', label: 'Margin rate' },
  ],
  attributes: [
    { name: 'customers.region', label: 'Customer region' },
    { name: 'customers.country', label: 'Customer country' },
    { name: 'products.department', label: 'Department' },
    { name: 'products.category', label: 'Product category' },
    { name: 'products.product', label: 'Product' },
    { name: 'stores.region', label: 'Store region' },
    { name: 'stores.channel_group', label: 'Channel' },
    { name: 'stores.store', label: 'Store' },
    { name: 'date.calendar_date', label: 'Date' },
    { name: 'date.year', label: 'Year' },
    { name: 'date.year_month', label: 'Month' },
  ],
}

interface Diagnostic { code: string; severity: 'error' | 'warning'; path: string; message: string }

/**
 * What the fixture source says in place of the engine for the parts of a query the browser cannot check (`ViewsClient.validate` is the one path to the server's catalog check): the attribute of each filter and
 * each order entry must be a member of the model's catalog, in the engine's words and at the entry's path. Only a stand-in for tests and the fixture source; the real source answers with the engine's own codes.
 */
export function catalogDiagnostics(doc: ViewsDoc | null, catalogs: Record<string, Catalog>): Diagnostic[] {
  const found: Diagnostic[] = []
  if (!doc) return found
  for (const [id, widget] of Object.entries(doc.widgets ?? {})) {
    const query = (widget as { query?: unknown }).query
    if (!isObject(query) || query.mode === 'explicit') continue
    const catalog = typeof query.model === 'string' ? catalogs[query.model] : undefined
    if (!catalog) continue
    const roles = catalogRoles(catalog)
    const check = (field: unknown, path: string, want: 'attribute' | 'any') => {
      if (typeof field !== 'string') return
      const role = roleOf(roles, field)
      if (role === 'unknown') found.push({ code: 'unknown_member', severity: 'error', path, message: `${field} is not a catalog member.` })
      else if (want === 'attribute' && role === 'measure') found.push({ code: 'wrong_member_kind', severity: 'error', path, message: `${field} is a measure, not a attribute.` })
    }
    if (Array.isArray(query.filters)) query.filters.forEach((f, i) => { if (isObject(f)) check(f.attribute, `widgets.${id}.query.filters[${i}].attribute`, 'attribute') })
    if (Array.isArray(query.order)) query.order.forEach((o, i) => check(Array.isArray(o) ? o[0] : isObject(o) ? o.by : undefined, `widgets.${id}.query.order[${i}]`, 'any'))
  }
  return found
}
