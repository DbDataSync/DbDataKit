/**
 * A value serialised so that two structurally equal values give the same string whatever the order of their keys: a control that rebuilds a value
 * with a spread reorders keys without changing meaning. `undefined` members are dropped; `null` is kept, because clearing a field is a change.
 * For change detection only, not a format anything parses.
 */
export function canonicalJson(value: unknown): string {
  return JSON.stringify(sortKeys(value))
}

function sortKeys(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(sortKeys)
  if (value && typeof value === 'object') {
    return Object.fromEntries(
      Object.entries(value as Record<string, unknown>)
        .filter(([, v]) => v !== undefined)
        .sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0))
        .map(([k, v]) => [k, sortKeys(v)]),
    )
  }
  return value
}
