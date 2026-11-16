/** The kinds a model can be started as (`project model create`): a copy, a native model and a mapped model are written by hand or imported. */
export const MODEL_KINDS: { key: string; label: string; about: string }[] = [
  { key: 'view', label: 'View', about: 'A query the database runs each time it is read.' },
  { key: 'full', label: 'Full table', about: 'A table rebuilt from its query on every refresh.' },
  { key: 'incremental_by_unique_key', label: 'Incremental by key', about: 'A table that adds new rows and replaces the rows whose key matches. Starts with the key `id`.' },
  { key: 'incremental_by_time_range', label: 'Incremental by time', about: 'A table loaded one time range at a time. Starts with the time column `event_ts`.' },
]

const MODEL_NAME = /^[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_]*$/

/** The sentence under the name box: nothing while it is empty or fine. */
export function modelNameProblem(name: string): string | null {
  if (name === '' || MODEL_NAME.test(name)) return null
  return 'A model is named schema_name.object_name, with letters, digits and underscores, for example marts.fct_returns.'
}
