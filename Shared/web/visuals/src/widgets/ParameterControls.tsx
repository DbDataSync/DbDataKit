import type { ControlDoc, ControlKind, ParameterDefinition, ParameterValue } from '../views/model'
import type { RenderedControl } from '../views/rendered'

/**
 * The parameter controls (plan 32 section 2, `control` widgets): each edits one parameter of the document; the host owns the values (`value`, `onChange`), the URL and the re-resolution of the widgets that use
 * them (DBR23). `select` and `multiselect` list the values the result gave (under the viewer's access), `date`, `daterange`, `number`, `text` and `toggle` need none. Every control has a label tied to it.
 */

export type ControlChange = (parameter: string, value: ParameterValue) => void

/** The kind a control has: the widget's own, else what the parameter suggests (many values: multiselect; a number; a flag; one with a list: select; else text). */
export function controlKind(widget: ControlDoc, definition?: ParameterDefinition): ControlKind {
  if (widget.control) return widget.control
  if (definition?.many) return 'multiselect'
  if (definition?.type === 'boolean') return 'toggle'
  if (definition?.type === 'integer' || definition?.type === 'number') return 'number'
  return widget.values || definition?.for ? 'select' : 'text'
}

const texts = (value: ParameterValue | undefined): string[] => (Array.isArray(value) ? value.map(String) : value === undefined || value === null || value === '' ? [] : [String(value)])
const noop: ControlChange = () => {}

export interface ControlWidgetProps {
  /** The widget's id in its document: it names the control's elements. */
  id: string
  widget: ControlDoc
  definition?: ParameterDefinition
  result?: RenderedControl
  value?: ParameterValue
  onChange?: ControlChange
}

export function ControlWidget({ id, widget, definition, result, value, onChange = noop }: ControlWidgetProps) {
  const kind = controlKind(widget, definition)
  const label = widget.label ?? definition?.label ?? widget.title ?? widget.parameter
  const name = widget.parameter
  const domId = `vz-control-${id}`
  const options = result?.values ?? []
  const set = (v: ParameterValue) => onChange(name, v)
  const typed = (raw: string): ParameterValue => (definition?.type === 'integer' ? (raw === '' ? null : Math.trunc(Number(raw))) : raw === '' ? null : Number(raw))

  let control
  switch (kind) {
    case 'select':
      control = (
        <select id={domId} className="vz-input" value={texts(value)[0] ?? ''} onChange={(e) => set(e.target.value === '' ? null : e.target.value)}>
          <option value="">{definition?.required ? 'Choose…' : 'All'}</option>
          {options.map((o) => <option key={String(o.value)} value={String(o.value)}>{o.label ?? String(o.value)}</option>)}
        </select>
      )
      break
    case 'multiselect': {
      const chosen = texts(value)
      control = (
        <div id={domId} className="vz-choices" role="group" aria-label={label}>
          {options.map((o) => {
            const v = String(o.value)
            return (
              <label key={v} className="vz-choice">
                <input type="checkbox" checked={chosen.includes(v)} onChange={(e) => set(e.target.checked ? [...chosen, v] : chosen.filter((c) => c !== v))} />
                {` ${o.label ?? v}`}
              </label>
            )
          })}
        </div>
      )
      break
    }
    case 'date':
      control = <input id={domId} className="vz-input" type="date" value={texts(value)[0] ?? ''} onChange={(e) => set(e.target.value === '' ? null : e.target.value)} />
      break
    case 'daterange': {
      const [from = '', to = ''] = texts(value)
      control = (
        <div id={domId} className="vz-choices" role="group" aria-label={label}>
          <label className="vz-choice">From <input className="vz-input" type="date" value={from} max={to || undefined} onChange={(e) => set([e.target.value, to])} /></label>
          <label className="vz-choice">To <input className="vz-input" type="date" value={to} min={from || undefined} onChange={(e) => set([from, e.target.value])} /></label>
        </div>
      )
      break
    }
    case 'number':
      control = <input id={domId} className="vz-input" type="number" value={texts(value)[0] ?? ''} onChange={(e) => set(typed(e.target.value))} />
      break
    case 'toggle':
      control = <input id={domId} type="checkbox" role="switch" checked={value === true} onChange={(e) => set(e.target.checked)} />
      break
    default:
      control = <input id={domId} className="vz-input" type="text" value={texts(value)[0] ?? ''} onChange={(e) => set(e.target.value === '' ? null : e.target.value)} />
  }
  const grouped = kind === 'multiselect' || kind === 'daterange'
  return (
    <div className={`vz-control vz-control-${kind}`} data-testid={`vz-control-${id}`}>
      {grouped ? <span className="vz-control-label">{label}</span> : <label className="vz-control-label" htmlFor={domId}>{label}</label>}
      {control}
    </div>
  )
}

export interface ParameterControlsProps {
  controls: Array<{ id: string; widget: ControlDoc; definition?: ParameterDefinition; result?: RenderedControl }>
  values: Record<string, ParameterValue>
  onChange?: ControlChange
}

/** A set of controls over the document's parameters: a parameters panel (DBR23), or the controls of a layout together. Pressing Enter in a field submits nothing. */
export function ParameterControls({ controls, values, onChange }: ParameterControlsProps) {
  return (
    <form className="vz-controls" aria-label="Parameters" onSubmit={(e) => e.preventDefault()}>
      {controls.map((c) => <ControlWidget key={c.id} id={c.id} widget={c.widget} definition={c.definition} result={c.result} value={values[c.widget.parameter]} onChange={onChange} />)}
    </form>
  )
}
