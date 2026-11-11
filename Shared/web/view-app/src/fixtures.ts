import type { EntitySchema, StoredRow } from '@dbdatakit/store-ui'
import type { GridSpec, ViewsDoc } from './spec'

const attr = (name: string, kind: string, extra: Record<string, unknown> = {}) => ({
  name, title: null, type: kind, kind, length: null, precision: null, scale: null, required: false, unique: false, default: null, pattern: null, min: null, max: null, allowed: null, references: null, ...extra,
})

export const territory = {
  name: 'territory', title: null, key: ['code'], history: 'none', role: 'master', rules: [],
  attributes: [attr('code', 'string', { required: true, length: 8 }), attr('name', 'string', { required: true, length: 80 }), attr('region', 'string', { length: 20 }), attr('quota', 'int32')],
} as unknown as EntitySchema

export const rows: StoredRow[] = [
  { code: 'EU', name: 'Europe', region: 'emea', quota: 40 },
  { code: 'NA', name: 'North America', region: 'amer', quota: 100 },
  { code: 'AP', name: 'Asia <Pacific>', region: 'apac', quota: null },
  { code: 'LA', name: 'Latin America', region: 'amer', quota: 25 },
].map((values, i) => ({ values, version: i + 1, valid_from: null, valid_to: null }))

export const grid: GridSpec = {
  kind: 'grid', title: 'Territories', model: 'territories', entity: 'territory', params: [], pageSize: 2,
  columns: [{ attribute: 'code', label: null, sortable: true }, { attribute: 'name', label: 'Territory', sortable: true }, { attribute: 'quota', label: null, sortable: false }],
  filters: [{ name: 'region', label: 'Sales region' }], sort: [{ attribute: 'name', direction: 'asc' }],
  links: [{ label: 'Open', to: 'views/detail.yaml', row: true, with: { id: { kind: 'attribute', text: 'code' } } }, { label: 'New', to: 'views/new.yaml', row: false, with: {} }],
}

export const doc: ViewsDoc = {
  format: 'view-spec/1', app: 'territory-views', instance: 'tv', title: 'Territories', home: 'views/grid.yaml', files: ['views/grid.yaml'], problems: [],
  views: { 'views/grid.yaml': grid },
}
