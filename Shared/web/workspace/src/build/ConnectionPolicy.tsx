import { parseYaml, removeAt, setAt } from '@dbdatakit/shell'

/**
 * The Gateway's serving policy as part of a published definition (plan 13c D19, owner decision of 2026-11-09): the `extensions.gateway` block of a connection file. A publish sends it and the Gateway replaces its own with exactly
 * this. `allow_writes` and `allowed_directories` are NOT offered here and are refused when the file carries them: they are not part of any definition, and a publish replaces the whole file on the Gateway.
 * Who may use the data source is `grants` (the grants editor), not `groups` or `everyone` here.
 */
export type PolicyValue = string | number | boolean

export interface PolicyField {
  key: string
  label: string
  kind: 'bool' | 'choice' | 'int'
  choices?: string[]
  min?: number
  max?: number
  hint: string
}

/** The fields this editor offers, in the order it draws them. Never `allow_writes` or `allowed_directories` (see `blockedFields`). */
export const policyFields: PolicyField[] = [
  { key: 'pass_through', label: 'Connect with the caller’s own login', kind: 'bool', hint: 'The database decides who may read; the Gateway passes the caller’s credentials through.' },
  { key: 'bitness', label: 'Driver bitness', kind: 'choice', choices: ['x64', 'x86'], hint: 'x86 goes through the 32-bit worker (ODBC only). Default: x64.' },
  { key: 'classifier', label: 'Statement check', kind: 'choice', choices: ['warn', 'strict', 'off'], hint: 'What the Gateway does with text that is not a single plain SELECT: warn and run it, refuse it, or skip the check.' },
  { key: 'metadata', label: 'Catalog metadata', kind: 'choice', choices: ['auto', 'odbc', 'informationschema'], hint: 'How the Gateway reads tables and columns. Default: auto.' },
  { key: 'default', label: 'The data source people get when they name none', kind: 'bool', hint: 'At most one data source of a Gateway can be the default.' },
  { key: 'max_concurrent_queries', label: 'Queries at once', kind: 'int', min: 0, max: 100000, hint: '0 is no limit.' },
  { key: 'max_concurrent_queries_per_user', label: 'Queries at once per user', kind: 'int', min: 0, max: 100000, hint: '0 is no limit.' },
  { key: 'acquire_timeout_seconds', label: 'Seconds a request waits for a free slot', kind: 'int', min: 1, max: 3600, hint: 'Only with a limit above.' },
  { key: 'batch_size', label: 'Rows per batch', kind: 'int', min: 1, max: 1000000, hint: 'Default: 32768.' },
]

/** The two fields that are not part of any definition: never offered, never published. */
export const blockedFields = ['allow_writes', 'allowed_directories'] as const

export interface ReadPolicy {
  /** The offered fields the file sets. A field the file does not set is the Gateway's default. */
  values: Record<string, PolicyValue>
  /** Keys of the block this editor does not draw (for example `compression`): edited in the YAML tab. */
  others: string[]
  /** The blocked fields the file carries; a publish is refused until they are taken out. */
  blocked: string[]
  /** Set when the block cannot be read by this editor; the YAML tab is the way then. */
  problem: string | null
}

const empty: ReadPolicy = { values: {}, others: [], blocked: [], problem: null }

/** What `extensions.gateway` of the file says. */
export function readPolicy(text: string): ReadPolicy {
  const parsed = parseYaml(text)
  if (parsed.errors.length > 0) return { ...empty, problem: 'The file is not valid YAML, so the policy cannot be shown here. Fix it in the YAML tab first.' }
  const extensions = (parsed.value as { extensions?: unknown } | undefined)?.extensions
  if (extensions === undefined || extensions === null) return empty
  if (typeof extensions !== 'object' || Array.isArray(extensions)) return { ...empty, problem: '`extensions` must be a mapping. Fix it in the YAML tab.' }
  const block = (extensions as { gateway?: unknown }).gateway
  if (block === undefined || block === null) return empty
  if (typeof block !== 'object' || Array.isArray(block)) return { ...empty, problem: '`extensions.gateway` must be a mapping of serving policy fields. Fix it in the YAML tab.' }
  const values: Record<string, PolicyValue> = {}
  const others: string[] = []
  const blocked: string[] = []
  for (const [key, value] of Object.entries(block as Record<string, unknown>)) {
    const field = policyFields.find((f) => f.key === key)
    if ((blockedFields as readonly string[]).includes(key)) blocked.push(key)
    else if (!field) others.push(key)
    else if (typeof value === 'boolean' || typeof value === 'number' || typeof value === 'string') values[key] = value
    else return { ...empty, problem: `\`extensions.gateway.${key}\` must be a single value. Fix it in the YAML tab.` }
  }
  return { values, others, blocked, problem: null }
}

/** What is wrong with a value for a field, as a sentence; null when it is fine or empty. */
export function valueProblem(field: PolicyField, value: PolicyValue | undefined): string | null {
  if (value === undefined || value === '') return null
  if (field.kind === 'int') {
    if (typeof value !== 'number' || !Number.isInteger(value)) return `${field.label} is a whole number.`
    if (value < (field.min ?? 0) || value > (field.max ?? Number.MAX_SAFE_INTEGER)) return `${field.label} is from ${field.min} to ${field.max}.`
  }
  if (field.kind === 'choice' && !field.choices?.includes(String(value))) return `${field.label} is one of ${field.choices?.join(', ')}.`
  if (field.kind === 'bool' && typeof value !== 'boolean') return `${field.label} is on or off.`
  return null
}

/**
 * The text with one field of the policy set, or taken out when `value` is undefined (the Gateway's default then). An empty `gateway` block, and an `extensions` that holds nothing else, go away with the last field.
 * Nothing else in the file changes. Only the offered fields can be set: asking for `allow_writes` or `allowed_directories` is an error, not an edit.
 */
export function writePolicy(text: string, key: string, value: PolicyValue | undefined): string {
  if ((blockedFields as readonly string[]).includes(key)) throw new Error(`${key} is not part of a definition, so it is never set here.`)
  if (!policyFields.some((f) => f.key === key)) throw new Error(`${key} is not a policy field this editor sets.`)
  if (value !== undefined) return setAt(text, ['extensions', 'gateway', key], value)
  let next = removeAt(text, ['extensions', 'gateway', key])
  const after = parseYaml(next).value as { extensions?: { gateway?: Record<string, unknown> } } | undefined
  if (after?.extensions?.gateway && Object.keys(after.extensions.gateway).length === 0) {
    next = removeAt(next, ['extensions', 'gateway'])
    const left = (parseYaml(next).value as { extensions?: Record<string, unknown> } | undefined)?.extensions
    if (left && Object.keys(left).length === 0) next = removeAt(next, ['extensions'])
  }
  return next
}

export interface ConnectionPolicyViewProps {
  policy: ReadPolicy
  readOnly: boolean
  onChange: (key: string, value: PolicyValue | undefined) => void
}

/** The serving policy of a connection, without its state: what the Gateway serves the data source with once it is published. */
export function ConnectionPolicyView({ policy, readOnly, onChange }: ConnectionPolicyViewProps) {
  const blocked = policy.problem !== null || readOnly
  return (
    <div className="card" style={{ marginBottom: 14 }} data-testid="build-policy">
      <div className="card-head"><span className="card-title">How the Gateway serves it</span></div>
      <div className="card-body">
        <div className="hint" data-testid="build-policy-note">
          This is the serving policy of the definition published to a Gateway: a publish replaces the Gateway’s policy for this data source completely. A field left unset is the Gateway’s default.
          Whether the data source may write and which directories it may read files from are not part of a definition: a data source published from here is read-only and reads no directories outside its database.
          Save the file, then publish the connection again for a change to reach the Gateway.
        </div>
        {policy.problem && <div className="hint" role="alert" style={{ marginTop: 6 }} data-testid="build-policy-problem">{policy.problem}</div>}
        {policy.blocked.length > 0 && (
          <div className="hint" role="alert" style={{ marginTop: 6 }} data-testid="build-policy-blocked">
            The file sets {policy.blocked.join(' and ')} under extensions.gateway. A definition never carries it, so the connection cannot be published until it is taken out in the YAML tab.
          </div>
        )}
        <div style={{ marginTop: 8 }}>
          {policyFields.map((field) => {
            const value = policy.values[field.key]
            const problem = valueProblem(field, value)
            return (
              <div key={field.key} className="row" style={{ gap: 10, marginBottom: 6, alignItems: 'center' }} data-testid={`build-policy-field-${field.key}`}>
                <label style={{ minWidth: 300 }} htmlFor={`build-policy-${field.key}`}>{field.label}</label>
                {field.kind === 'bool' && (
                  <input id={`build-policy-${field.key}`} type="checkbox" checked={value === true} disabled={blocked} onChange={(e) => onChange(field.key, e.target.checked ? true : undefined)} data-testid={`build-policy-input-${field.key}`} />
                )}
                {field.kind === 'choice' && (
                  <select id={`build-policy-${field.key}`} className="input" style={{ width: 180 }} value={value === undefined ? '' : String(value)} disabled={blocked} onChange={(e) => onChange(field.key, e.target.value === '' ? undefined : e.target.value)} data-testid={`build-policy-input-${field.key}`}>
                    <option value="">Gateway default</option>
                    {field.choices?.map((c) => <option key={c} value={c}>{c}</option>)}
                  </select>
                )}
                {field.kind === 'int' && (
                  <input id={`build-policy-${field.key}`} className="input mono" style={{ width: 120 }} type="number" min={field.min} max={field.max} placeholder="default" value={value === undefined ? '' : String(value)} disabled={blocked}
                    onChange={(e) => { const n = e.target.value === '' ? undefined : Number(e.target.value); if (n === undefined || valueProblem(field, n) === null) onChange(field.key, n) }} data-testid={`build-policy-input-${field.key}`} />
                )}
                <span className="hint">{problem ?? field.hint}</span>
              </div>
            )
          })}
        </div>
        {policy.others.length > 0 && <div className="hint" style={{ marginTop: 6 }} data-testid="build-policy-others">Also in the file, and edited in the YAML tab: {policy.others.join(', ')}.</div>}
      </div>
    </div>
  )
}

/** The serving policy of the open connection file, edited in the file's text (the form and the YAML tab show the same text). */
export function ConnectionPolicyEditor({ text, onText, readOnly = false }: { text: string; onText: (text: string) => void; readOnly?: boolean }) {
  const policy = readPolicy(text)
  const change = (key: string, value: PolicyValue | undefined) => { try { onText(writePolicy(text, key, value)) } catch { /* the text is not valid YAML: the problem line says so */ } }
  return <ConnectionPolicyView policy={policy} readOnly={readOnly} onChange={change} />
}
