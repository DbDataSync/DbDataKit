import type { SubTab } from '@dbdatakit/shell'

/** The mapping editor's tabs in their order, each at `<mapping>/<path>`; `drawn` says which this Designer draws (the others' keys are edited in Files). */
export const MAPPING_TABS: (SubTab & { drawn: boolean })[] = [
  { path: 'notes', label: 'Notes', testId: 'mapping-tab-notes', drawn: false },
  { path: 'columns', label: 'Column Mapping', testId: 'mapping-tab-columns', drawn: true },
  { path: 'transforms', label: 'Custom Transforms', testId: 'mapping-tab-transforms', drawn: false },
  { path: 'segmenting', label: 'Bulk Load', testId: 'mapping-tab-segmenting', drawn: false },
  { path: 'provisioning', label: 'Provisioning', testId: 'mapping-tab-provisioning', drawn: false },
  { path: 'pipeline', label: 'Pipeline', testId: 'mapping-tab-pipeline', drawn: false },
  { path: 'diagnostics', label: 'Diagnostics', testId: 'mapping-tab-diagnostics', drawn: false },
]

/** The tabs a mapping's editor shows, linked under `base` (the mapping's address). */
export function mappingTabs(base: string): SubTab[] {
  return MAPPING_TABS.filter((t) => t.drawn).map(({ drawn: _drawn, ...t }) => ({ ...t, to: `${base}/${t.path}` }))
}

/** The tab an address names, the first drawn one when it names none or one not drawn. */
export const tabOrDefault = (tab: string) => (MAPPING_TABS.some((t) => t.drawn && t.path === tab) ? tab : 'columns')
