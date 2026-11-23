import { ErrorBanner, Field, KeyValueTable } from '@dbdatakit/shell'
import { useScriptSlots } from '../api/hooks'
import type { BindingLevel, ScriptSlotInfo } from '../api/types'
import { useSyncScripts } from '../useSyncDraft'
import { ParameterForm } from './ParameterForm'
import type { ScriptBinding, ScriptBindings, ScriptView } from '../types'

/**
 * The scripts bound to slots at one level: a connection, a replication or a table mapping. A slot has three states, and they are three:
 *
 * - **Inherit**: this level says nothing and a broader level's binding applies (the key is absent).
 * - **A script**: this level binds one, replacing anything inherited, parameters and all.
 * - **None**: this level binds nothing, overriding an inherited script (the key is there with a null value).
 *
 * The slots come from the Workspace (`sync/script-slots`), the scripts from the draft's `config/scripts/` (edited in Files).
 */
export function ScriptBindingsCard({ bindings, inherited, level, onChange, editable = true }: {
  bindings: ScriptBindings
  /** What broader levels bind, per slot, for the INHERITED line; empty at the connection level. */
  inherited: ScriptBindings
  level: BindingLevel
  onChange: (next: ScriptBindings) => void
  editable?: boolean
}) {
  const slots = useScriptSlots()
  const { scripts } = useSyncScripts()
  return (
    <>
      <ErrorBanner error={slots.error} />
      <ScriptBindingsView bindings={bindings} inherited={inherited} level={level} slots={slots.data} scripts={scripts} onChange={onChange} editable={editable} />
    </>
  )
}

/** The card without its reads: the slots and the draft's scripts are given. */
export function ScriptBindingsView({ bindings, inherited, level, slots, scripts, onChange, editable = true }: {
  bindings: ScriptBindings
  inherited: ScriptBindings
  level: BindingLevel
  /** Undefined while the slots are being read. */
  slots: ScriptSlotInfo[] | undefined
  scripts: ScriptView[]
  onChange: (next: ScriptBindings) => void
  editable?: boolean
}) {
  // Only the slots that mean something at this level: a metadata provider bound on a mapping would never be asked.
  const applicable = (slots ?? []).filter((s) => s.levels.includes(level))
  const boundHere = applicable.filter((s) => s.slot in bindings)
  const inheritedFrom = level === 'mapping' ? 'the replication or connection' : level === 'replication' ? 'the source connection' : null

  return (
    <div className="card" data-testid="script-bindings-card">
      <div className="card-head">
        <span className="card-title" data-testid="script-bindings-title">Custom transforms and providers</span>
        <span className="card-note" data-testid="script-bindings-note">
          {boundHere.length > 0
            ? `${summarise(boundHere, bindings)} · ${level === 'connection' ? 'applied wherever this connection is read' : `overriding ${inheritedFrom}`}`
            : level === 'connection'
              ? 'C# that runs for every replication reading through this connection; none bound'
              : `C# that changes what this level reads or writes; none bound, inherited from ${inheritedFrom}`}
        </span>
      </div>
      <div className="card-body">
        {slots === undefined && <span className="hint">Reading the script slots…</span>}
        {applicable.map(({ slot, label, description }) => (
          <SlotBinding
            key={slot}
            slot={slot}
            label={label}
            description={description}
            binding={slot in bindings ? bindings[slot] : undefined}
            inherited={inherited[slot] ?? null}
            available={scripts.filter((s) => s.kind === slot && s.enabled)}
            editable={editable}
            onChange={(next) => {
              const copy = { ...bindings }
              if (next === undefined) delete copy[slot]
              else copy[slot] = next
              onChange(copy)
            }}
          />
        ))}
        {slots && applicable.length === 0 && <span className="hint">No script slots apply at this level.</span>}
      </div>
    </div>
  )
}

const INHERIT = '__inherit__'
const NONE = '__none__'

/** The slots bound here by name ("Row transform, Value column expression (none)"): what is happening, not a count. */
function summarise(boundHere: ScriptSlotInfo[], bindings: ScriptBindings): string {
  return boundHere.map((s) => (bindings[s.slot] === null ? `${s.label} (none)` : s.label)).join(', ')
}

function SlotBinding({ slot, label, description, binding, inherited, available, editable, onChange }: {
  slot: string
  label: string
  description: string
  /** undefined: the key is absent, so this level inherits. null: none, explicitly. */
  binding: ScriptBinding | null | undefined
  inherited: ScriptBinding | null
  /** The enabled scripts that fill this slot; a binding's parameter form is drawn from its script's declaration. */
  available: ScriptView[]
  editable: boolean
  onChange: (next: ScriptBinding | null | undefined) => void
}) {
  const declared = available.find((s) => s.name === binding?.scriptName)?.parameters ?? []
  const value = binding === undefined ? INHERIT : binding === null ? NONE : binding.scriptName
  const known = binding ? available.some((s) => s.name === binding.scriptName) : true

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 9 }} data-testid={`script-slot-${slot}`}>
      <div className="row" style={{ gap: 8 }}>
        <span className="card-title sm">{label}</span>
        {binding === undefined && <span className="badge">INHERITED</span>}
      </div>
      {description && <span className="hint">{description}</span>}

      <Field label="Script">
        <select
          className="select"
          value={value}
          disabled={!editable}
          onChange={(e) => {
            if (e.target.value === INHERIT) onChange(undefined)
            else if (e.target.value === NONE) onChange(null)
            else onChange({ scriptName: e.target.value, parameters: binding?.parameters ?? {} })
          }}
          data-testid={`script-binding-${slot}`}
        >
          <option value={INHERIT}>{inherited ? `Inherit: ${inherited.scriptName}` : 'Inherit: nothing bound'}</option>
          <option value={NONE}>None (override)</option>
          {/* a script the draft does not have (or that is disabled, or fills another slot) is kept and named, as the file says it */}
          {!known && binding && <option value={binding.scriptName}>{binding.scriptName}, not an enabled script of this slot</option>}
          {available.map((s) => <option key={s.name} value={s.name}>{s.name}</option>)}
        </select>
      </Field>

      {binding && declared.length > 0 && (
        <ParameterForm parameters={declared} values={binding.parameters ?? {}} onChange={(parameters) => onChange({ ...binding, parameters })} testIdPrefix={`script-parameters-${slot}`} disabled={!editable} />
      )}

      {/* a script that declares nothing still takes parameters: the free-form table is for it */}
      {binding && declared.length === 0 && editable && (
        <Field label="Parameters">
          <KeyValueTable value={binding.parameters ?? {}} onChange={(parameters) => onChange({ ...binding, parameters })} addLabel="Parameter name" testId={`script-parameters-${slot}`} />
        </Field>
      )}
    </div>
  )
}
