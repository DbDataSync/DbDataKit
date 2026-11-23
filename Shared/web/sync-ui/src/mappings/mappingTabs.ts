import type { SubTab } from '@dbdatakit/shell'

/** The mapping editor's tabs in their order, each at `<mapping>/<path>`. */
export const MAPPING_TABS: SubTab[] = [
  { path: 'notes', label: 'Notes', testId: 'mapping-tab-notes' },
  { path: 'columns', label: 'Column Mapping', testId: 'mapping-tab-columns' },
  { path: 'transforms', label: 'Custom Transforms', testId: 'mapping-tab-transforms' },
  { path: 'segmenting', label: 'Bulk Load', testId: 'mapping-tab-segmenting' },
  { path: 'provisioning', label: 'Provisioning', testId: 'mapping-tab-provisioning' },
  { path: 'pipeline', label: 'Pipeline', testId: 'mapping-tab-pipeline' },
  { path: 'diagnostics', label: 'Diagnostics', testId: 'mapping-tab-diagnostics' },
  { path: 'verify', label: 'Verify', testId: 'mapping-tab-verify' },
]

/**
 * The tabs a mapping's editor shows, linked under `base` (the mapping's address). The Provisioning tab carries the count of statements the
 * mapping's plan would run, so a missing table shows without opening the tab.
 */
export function mappingTabs(base: string, pendingProvisioning = 0): SubTab[] {
  return MAPPING_TABS.map((t) => ({ ...t, to: `${base}/${t.path}`, ...(t.path === 'provisioning' ? { badge: pendingProvisioning } : {}) }))
}

/** The tab an address names; Column Mapping when it names none or one there is not. */
export const tabOrDefault = (tab: string) => (MAPPING_TABS.some((t) => t.path === tab) ? tab : 'columns')
