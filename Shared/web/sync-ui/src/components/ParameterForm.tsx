import { CodeEditor, Field, KeyValueTable } from '@dbdatakit/shell'
import { isVararg, occurrences, varargEntries, withVararg } from './parameterValues'
import type { ParameterDescriptor } from '../types'

/**
 * The form of whatever a reader, staging provider, writer or script declares: the driver or script says what exists and this draws it, so a new
 * one needs no change here. The values are the flat string dictionary the file holds (`options:` of a stage, `parameters:` of a binding).
 */
export function ParameterForm({ parameters, values, onChange, options, testIdPrefix, disabled = false }: {
  parameters: ParameterDescriptor[]
  values: Record<string, string>
  onChange: (next: Record<string, string>) => void
  /** Choices the screen knows and the declaration cannot (the mapping's columns for a column picker, the draft's scripts), keyed by parameter name. */
  options?: Record<string, string[]>
  testIdPrefix: string
  disabled?: boolean
}) {
  // What the declarer says applies now; the rule is the declarer's, never repeated here.
  const shown = parameters.filter((p) => p.visible !== false)

  if (shown.length === 0)
    return <span className="hint" data-testid={`${testIdPrefix}-none`}>This choice has no settings.</span>

  // Cards keep their declared order (first mention wins), so an author lays a form out by the order they declare it in.
  const cards = [...new Set(shown.map((p) => p.layout?.card ?? ''))]

  return (
    <fieldset disabled={disabled} style={{ display: 'flex', flexDirection: 'column', gap: 12, border: 0, padding: 0, margin: 0 }} data-testid={testIdPrefix}>
      {cards.map((card) => (
        <div key={card} style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
          {card && <span className="card-title sm">{card}</span>}
          {groupsIn(shown, card).map((group, i) => (
            <div key={i} style={{ display: 'flex', gap: 12, alignItems: 'flex-start' }}>
              {group.map((parameter) => (
                <div key={parameter.name} style={{ flex: parameter.layout?.size ?? 1, minWidth: 0 }}>
                  <Parameter parameter={parameter} values={values} onChange={onChange} options={options?.[parameter.name]} testIdPrefix={testIdPrefix} />
                </div>
              ))}
            </div>
          ))}
        </div>
      ))}
    </fieldset>
  )
}

/** Parameters of one group name sit on one row; a parameter of no group has a row of its own. */
function groupsIn(parameters: ParameterDescriptor[], card: string): ParameterDescriptor[][] {
  const rows: ParameterDescriptor[][] = []
  for (const parameter of parameters.filter((p) => (p.layout?.card ?? '') === card)) {
    const group = parameter.layout?.group ?? ''
    const existing = group ? rows.find((r) => (r[0]!.layout?.group ?? '') === group) : undefined
    if (existing) existing.push(parameter)
    else rows.push([parameter])
  }
  return rows
}

function Parameter({ parameter, values, onChange, options, testIdPrefix }: {
  parameter: ParameterDescriptor
  values: Record<string, string>
  onChange: (next: Record<string, string>) => void
  options?: string[]
  testIdPrefix: string
}) {
  const label = parameter.label || parameter.name
  const testId = `${testIdPrefix}-${parameter.name}`

  if (isVararg(parameter)) {
    const { min, max } = occurrences(parameter)
    const entries = varargEntries(parameter, values)
    const count = Object.keys(entries).length
    return (
      <Field label={parameter.required ? `${label} *` : label}>
        {parameter.description && <span className="hint">{parameter.description}</span>}
        <KeyValueTable value={entries} onChange={(next) => onChange(withVararg(parameter, values, next))} addLabel={`${label} name`} testId={testId} />
        {(count < min || count > max) && (
          <span className="hint" style={{ color: 'var(--danger-ink)' }} data-testid={`${testId}-bound`}>
            {count < min ? `At least ${min} needed; ${count} set.` : `At most ${max} allowed; ${count} set.`}
          </span>
        )}
      </Field>
    )
  }

  const value = values[parameter.name] ?? parameter.default ?? ''
  const set = (next: string) => onChange({ ...values, [parameter.name]: next })

  return (
    <Field label={parameter.required ? `${label} *` : label}>
      {parameter.description && <span className="hint">{parameter.description}</span>}
      <Control parameter={parameter} value={value} set={set} options={options} testId={testId} />
      {parameter.required && !value && (
        <span className="hint" style={{ color: 'var(--danger-ink)' }} data-testid={`${testId}-required`}>Required.</span>
      )}
    </Field>
  )
}

function Control({ parameter, value, set, options, testId }: {
  parameter: ParameterDescriptor
  value: string
  set: (next: string) => void
  options?: string[]
  testId: string
}) {
  // A dropdown's choices come from the declaration when it knows them, else from the screen.
  const choices = parameter.dropdownOptions ?? options

  switch (parameter.type) {
    case 'Bool':
      return (
        <span className="row" style={{ gap: 7, height: 30 }}>
          <button type="button" className={`toggle ${value === 'true' ? 'on' : ''}`} aria-pressed={value === 'true'} onClick={() => set(value === 'true' ? 'false' : 'true')} data-testid={testId} />
          <span style={{ font: '500 11.5px var(--ui)', color: 'var(--ink-4)' }}>{value === 'true' ? 'On' : 'Off'}</span>
        </span>
      )

    case 'Dropdown':
    case 'ColumnPicker':
      return (
        <select className="select" value={value} onChange={(e) => set(e.target.value)} data-testid={testId}>
          <option value="">Select…</option>
          {/* A value the choices no longer offer is kept and labelled: it is what the file says, and dropping it would be a change nobody made. */}
          {value && !(choices ?? []).includes(value) && <option value={value}>{value}, not offered</option>}
          {(choices ?? []).map((c) => <option key={c} value={c}>{parameter.dropdownLabels?.[c] ?? c}</option>)}
        </select>
      )

    case 'Secret':
      // The file holds a reference to a secret, never the secret: the draft's check refuses a value that is not one.
      return <input className="input mono" placeholder="secret:<name>" value={value} onChange={(e) => set(e.target.value)} data-testid={testId} />

    case 'Sql':
      return <CodeEditor value={value} language="sql" onChange={set} minLines={6} testId={testId} />

    case 'Number':
      return <input className="input" type="number" value={value} onChange={(e) => set(e.target.value)} data-testid={testId} />

    case 'Date':
    case 'DateTime':
      return <input className="input" type={parameter.type === 'Date' ? 'date' : 'datetime-local'} value={value} onChange={(e) => set(e.target.value)} data-testid={testId} />

    default:
      return <input className="input" value={value} onChange={(e) => set(e.target.value)} data-testid={testId} />
  }
}
