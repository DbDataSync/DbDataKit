import type { RowValues } from '@dbdatakit/store-ui'

/**
 * The key a detail or an edit view addresses its row by (plan 26 AP28). An entity with one key attribute is addressed by the view's `key` parameter, as before.
 * An entity with a composite key is addressed by one parameter **per key attribute, named as the attribute** (the view declares them; `key` names one of them, and a link supplies all of them in `with`).
 * A view that lacks one of them is refused in words that name it, never guessed.
 */
export function rowKey(entity: { key: string[]; name: string }, keyParam: string | null, params: Record<string, string>): { key: RowValues; label: string } | Error {
  if (entity.key.length === 1) {
    const value = keyParam ? params[keyParam] : undefined
    return { key: { [entity.key[0]]: value }, label: value ?? '' }
  }
  const missing = entity.key.filter((a) => params[a] === undefined)
  if (missing.length > 0) {
    return new Error(`'${entity.name}' has a key of ${entity.key.length} attributes (${entity.key.join(', ')}); the view needs a parameter for each, named as the attribute, and lacks ${missing.map((m) => `'${m}'`).join(', ')}.`)
  }
  return { key: Object.fromEntries(entity.key.map((a) => [a, params[a]])), label: entity.key.map((a) => params[a]).join(', ') }
}
