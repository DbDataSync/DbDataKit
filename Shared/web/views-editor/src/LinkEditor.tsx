import { useState } from 'react'
import { LINK_AS, LINK_PRINT, LINK_TARGETS, NAME_PATTERN, type ClientDiagnostic, type EditCommand, type LinkDoc, type LinkTargetKind, type NewAction, type ViewsDoc } from '@dbdatakit/visuals'

type Apply = (command: EditCommand, group?: string) => void

const LINK_NAME = /^[a-z][a-z0-9_-]{0,63}$/
const VALUE_NAME = /^[A-Za-z][A-Za-z0-9_-]{0,63}$/

/** The value a typed text makes: `true` and `false` and plain numbers are literals of that type, anything else (`$orders.id`, `$params.region`, a word) is text. Quote nothing: a text that looks like a number is a number here, and a person who wants the text writes it in the YAML view. */
export function parseLinkValue(text: string): string | number | boolean {
  const t = text.trim()
  if (t === 'true') return true
  if (t === 'false') return false
  return /^-?\d+(\.\d+)?$/.test(t) ? Number(t) : t
}

export const showLinkValue = (v: unknown): string => String(v)

const targetOf = (link: LinkDoc): LinkTargetKind | undefined => LINK_TARGETS.find((k) => typeof link[k] === 'string')

function Problems({ diagnostics, testId }: { diagnostics: ClientDiagnostic[]; testId: string }) {
  if (diagnostics.length === 0) return null
  return <ul className="hint" data-testid={testId} style={{ margin: '2px 0' }}>{diagnostics.map((d, i) => <li key={i} role="alert">{d.code} {d.message}</li>)}</ul>
}

type Section = Record<string, unknown>

/**
 * An action link (plan 32 DBR53): what it does is shown in words (`change` of `model.entity`, or a `task`), its values are edited as a link's are, and the popup's words (title and button), `stage`, `for` and `skip` are controls.
 * The `ask` fields and a key's shape are YAML (this row never rewrites them).
 */
function ActionRow({ name, link, doc, diagnostics, apply }: { name: string; link: LinkDoc; doc: ViewsDoc; diagnostics: ClientDiagnostic[]; apply: Apply }) {
  const [field, setField] = useState('')
  const [value, setValue] = useState('')
  const change = link.change as Section | undefined
  const task = link.task as Section | undefined
  const operation = task ? 'run' : change?.insert !== undefined ? 'insert' : change?.delete !== undefined ? 'delete' : 'edit'
  const values = ((task ? task.inputs : operation === 'insert' ? change?.insert : change?.set) ?? {}) as Record<string, unknown>
  const confirm = typeof link.confirm === 'object' && link.confirm !== null ? (link.confirm as Section) : {}
  const skip = link.confirm === 'skip'
  const widgets = Object.keys(doc.widgets ?? {})
  const thenList = Array.isArray(link.then)
  const option = (key: 'then' | 'print' | 'image' | 'label' | 'stage' | 'for', v: unknown) => apply({ type: 'setLinkOption', name, key, value: v }, `link:${name}:${key}`)
  const mine = diagnostics.filter((d) => d.path === `links.${name}` || d.path.startsWith(`links.${name}.`) || d.path.startsWith(`links.${name}[`))
  return (
    <div className="card" style={{ margin: '6px 0' }} data-testid={`ed-link-${name}`} data-action="">
      <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
        <code style={{ minWidth: 100 }}>{name}</code>
        <span className="hint" data-testid={`ed-action-what-${name}`}>{task ? `runs the task ${String(task.package)}.${String(task.task)}` : `${operation === 'insert' ? 'adds a row to' : operation === 'delete' ? 'deletes a row of' : 'changes a row of'} ${String(change?.model)}.${String(change?.entity)}`}</span>
        <label className="hint">popup title <input aria-label={`${name} popup title`} data-testid={`ed-action-title-${name}`} style={{ width: 200 }} placeholder="Close order {{orders.number}}" disabled={skip} value={String(confirm.title ?? '')} onChange={(e) => apply({ type: 'setActionConfirm', name, key: 'title', value: e.target.value }, `link:${name}:confirm.title`)} /></label>
        <label className="hint">button <input aria-label={`${name} popup button`} data-testid={`ed-action-button-${name}`} style={{ width: 120 }} disabled={skip} value={String(confirm.button ?? '')} onChange={(e) => apply({ type: 'setActionConfirm', name, key: 'button', value: e.target.value }, `link:${name}:confirm.button`)} /></label>
        <button type="button" className="btn btn-sm" data-testid={`ed-link-remove-${name}`} onClick={() => apply({ type: 'removeLink', name })}>Remove</button>
      </div>
      <div className="row hint" style={{ gap: 12, alignItems: 'center', flexWrap: 'wrap', marginTop: 4 }}>
        {change && (
          <>
            <label><input type="checkbox" data-testid={`ed-action-stage-${name}`} checked={link.stage === true} disabled={skip} onChange={(e) => option('stage', e.target.checked ? true : undefined)} /> stage in the set (one review for many)</label>
            <label>for{' '}
              <select aria-label={`${name} for`} data-testid={`ed-action-for-${name}`} value={link.for ?? 'row'} disabled={operation === 'insert'} onChange={(e) => option('for', e.target.value === 'row' ? undefined : e.target.value)}>
                <option value="row">the clicked row</option>
                <option value="selection">each selected row</option>
              </select>
            </label>
            <label><input type="checkbox" data-testid={`ed-action-skip-${name}`} checked={skip} disabled={operation !== 'edit'} onChange={(e) => apply({ type: 'setActionConfirm', name, key: 'skip', value: e.target.checked })} /> apply a field edit at once (no popup)</label>
          </>
        )}
        <label>then{' '}
          <select aria-label={`${name} then`} data-testid={`ed-link-then-${name}`} value={thenList ? 'widgets' : String(link.then ?? '')} onChange={(e) => option('then', e.target.value === '' ? undefined : e.target.value === 'widgets' ? widgets.slice(0, 1) : e.target.value)}>
            <option value="">(default: refresh)</option>
            <option value="refresh">refresh the source widget</option>
            <option value="none">nothing</option>
            <option value="widgets" disabled={widgets.length === 0}>these widgets</option>
          </select>
        </label>
        <label>PDF and print{' '}
          <select aria-label={`${name} print`} data-testid={`ed-link-print-${name}`} value={link.print ?? ''} onChange={(e) => option('print', e.target.value === '' ? undefined : e.target.value)}>
            <option value="">(by meaning: an action does not print)</option>
            <option value="normal">normal</option>
            <option value="hidden">hidden</option>
          </select>
        </label>
        <label>label <input aria-label={`${name} label`} data-testid={`ed-link-label-${name}`} style={{ width: 140 }} value={link.label ?? ''} onChange={(e) => option('label', e.target.value === '' ? undefined : e.target.value)} /></label>
      </div>
      {thenList && (
        <div className="row hint" style={{ gap: 8, flexWrap: 'wrap' }} data-testid={`ed-link-then-widgets-${name}`}>
          refresh:{' '}
          {widgets.map((w) => (
            <label key={w}><input type="checkbox" data-testid={`ed-link-then-widget-${name}-${w}`} checked={(link.then as string[]).includes(w)} onChange={(e) => {
              const now = new Set(link.then as string[])
              if (e.target.checked) now.add(w); else now.delete(w)
              option('then', now.size === 0 ? undefined : widgets.filter((x) => now.has(x)))
            }} /> {w}</label>
          ))}
        </div>
      )}
      {operation !== 'delete' && (
        <>
          <table className="table" style={{ marginTop: 4 }}>
            <tbody>
              {Object.entries(values).map(([k, v]) => (
                <tr key={k} data-testid={`ed-action-value-row-${name}-${k}`}>
                  <td><code>{k}</code></td>
                  <td><input aria-label={`${name} ${k}`} data-testid={`ed-action-value-${name}-${k}`} value={showLinkValue(v)} onChange={(e) => apply({ type: 'setActionValue', name, field: k, value: parseLinkValue(e.target.value) }, `link:${name}:value:${k}`)} /></td>
                  <td><button type="button" className="btn btn-sm" data-testid={`ed-action-value-remove-${name}-${k}`} onClick={() => apply({ type: 'setActionValue', name, field: k, value: undefined })}>Remove</button></td>
                </tr>
              ))}
              {Object.keys(values).length === 0 && <tr><td className="hint">Writes {task ? 'no inputs' : 'no values'}.</td></tr>}
            </tbody>
          </table>
          <div className="row" style={{ gap: 6, alignItems: 'center' }}>
            <input aria-label={`${name} new field`} data-testid={`ed-action-field-${name}`} placeholder={task ? 'input' : 'field'} value={field} onChange={(e) => setField(e.target.value)} />
            <input aria-label={`${name} new field value`} data-testid={`ed-action-field-value-${name}`} placeholder="Closed, $orders.region or $ask.status" value={value} onChange={(e) => setValue(e.target.value)} />
            <button type="button" className="btn btn-sm" data-testid={`ed-action-field-add-${name}`} disabled={!VALUE_NAME.test(field) || value.trim() === ''} onClick={() => { apply({ type: 'setActionValue', name, field, value: parseLinkValue(value) }); setField(''); setValue('') }}>Add value</button>
          </div>
        </>
      )}
      <Problems diagnostics={mine} testId={`ed-link-problems-${name}`} />
    </div>
  )
}

/** Adds an action link (plan 32 DBR53): a change of a Store row or a runner task, with the one value it writes; the rest (the popup's words, `ask`, `stage`) is set on its row after. */
function ActionAdder({ links, apply }: { links: Record<string, LinkDoc>; apply: Apply }) {
  const [name, setName] = useState('')
  const [kind, setKind] = useState<'edit' | 'insert' | 'delete' | 'task'>('edit')
  const [first, setFirst] = useState('')
  const [second, setSecond] = useState('')
  const [key, setKey] = useState('')
  const [field, setField] = useState('')
  const [value, setValue] = useState('')
  const task = kind === 'task'
  const row = kind === 'edit' || kind === 'delete'
  const ready = LINK_NAME.test(name) && !(name in links) && first.trim() !== '' && second.trim() !== '' && (!row || key.trim() !== '')
  const add = () => {
    const values = VALUE_NAME.test(field) && value.trim() !== '' ? { [field]: parseLinkValue(value) } : undefined
    const action: NewAction = task
      ? { kind: 'task', package: first.trim(), task: second.trim(), ...(values ? { inputs: values } : {}) }
      : { kind: 'change', model: first.trim(), entity: second.trim(), operation: kind, ...(row ? { key: key.trim() } : {}), ...(values ? { values } : {}) }
    apply({ type: 'addActionLink', name, action })
    setName(''); setFirst(''); setSecond(''); setKey(''); setField(''); setValue('')
  }
  return (
    <div className="row" style={{ gap: 6, alignItems: 'center', flexWrap: 'wrap', marginTop: 8 }} data-testid="ed-action-add-form">
      <input aria-label="New action name" data-testid="ed-action-new-name" placeholder="close-order" value={name} onChange={(e) => setName(e.target.value)} />
      <select aria-label="New action kind" data-testid="ed-action-new-kind" value={kind} onChange={(e) => setKind(e.target.value as typeof kind)}>
        <option value="edit">change a row</option>
        <option value="insert">add a row</option>
        <option value="delete">delete a row</option>
        <option value="task">run a task</option>
      </select>
      <input aria-label={task ? 'Package' : 'Model'} data-testid="ed-action-new-first" placeholder={task ? 'package' : 'model'} value={first} onChange={(e) => setFirst(e.target.value)} />
      <input aria-label={task ? 'Task' : 'Entity'} data-testid="ed-action-new-second" placeholder={task ? 'task' : 'entity'} value={second} onChange={(e) => setSecond(e.target.value)} />
      {row && <input aria-label="Row key" data-testid="ed-action-new-key" placeholder="$orders.id" value={key} onChange={(e) => setKey(e.target.value)} />}
      {kind !== 'delete' && <input aria-label={task ? 'Input' : 'Field'} data-testid="ed-action-new-field" placeholder={task ? 'input' : 'field'} value={field} onChange={(e) => setField(e.target.value)} />}
      {kind !== 'delete' && <input aria-label="Value" data-testid="ed-action-new-value" placeholder="Closed or $orders.region" value={value} onChange={(e) => setValue(e.target.value)} />}
      <button type="button" className="btn btn-sm" data-testid="ed-action-add" disabled={!ready} onClick={add}>Add action</button>
    </div>
  )
}

function LinkRow({ name, link, doc, diagnostics, apply }: { name: string; link: LinkDoc; doc: ViewsDoc; diagnostics: ClientDiagnostic[]; apply: Apply }) {
  const [param, setParam] = useState('')
  const [value, setValue] = useState('')
  const kind = targetOf(link) ?? 'page'
  const isForm = kind === 'form'
  const mode = link.edit !== undefined && link.new === undefined ? 'edit' : 'new'
  const values = ((isForm ? link[mode] : link.with) ?? {}) as Record<string, unknown>
  const locked = new Set(link.lock ?? [])
  const widgets = Object.keys(doc.widgets ?? {})
  const thenList = Array.isArray(link.then)
  const option = (key: 'as' | 'then' | 'print' | 'image' | 'label', v: unknown) => apply({ type: 'setLinkOption', name, key, value: v }, `link:${name}:${key}`)
  const mine = diagnostics.filter((d) => d.path === `links.${name}` || d.path.startsWith(`links.${name}.`) || d.path.startsWith(`links.${name}[`))
  return (
    <div className="card" style={{ margin: '6px 0' }} data-testid={`ed-link-${name}`}>
      <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
        <code style={{ minWidth: 100 }}>{name}</code>
        <label className="hint">leads to{' '}
          <select aria-label={`${name} target kind`} data-testid={`ed-link-kind-${name}`} value={kind} onChange={(e) => apply({ type: 'setLinkTarget', name, kind: e.target.value as LinkTargetKind, target: link[kind] ?? '' }, `link:${name}:kind`)}>
            {LINK_TARGETS.map((t) => <option key={t} value={t}>{t}</option>)}
          </select>
        </label>
        <input aria-label={`${name} target`} data-testid={`ed-link-target-${name}`} style={{ width: 150 }} placeholder="name in the app" value={link[kind] ?? ''}
          onChange={(e) => { if (LINK_NAME.test(e.target.value)) apply({ type: 'setLinkTarget', name, kind, target: e.target.value }, `link:${name}:target`) }} />
        {isForm && (
          <label className="hint">a row to{' '}
            <select aria-label={`${name} form mode`} data-testid={`ed-link-mode-${name}`} value={mode} onChange={(e) => apply({ type: 'setFormMode', name, mode: e.target.value as 'new' | 'edit' })}>
              <option value="new">create (new)</option>
              <option value="edit">change (edit)</option>
            </select>
          </label>
        )}
        <label className="hint">shown as{' '}
          <select aria-label={`${name} presentation`} data-testid={`ed-link-as-${name}`} value={link.as ?? 'navigate'} onChange={(e) => option('as', e.target.value === 'navigate' ? undefined : e.target.value)}>
            {LINK_AS.map((a) => <option key={a} value={a}>{a}</option>)}
          </select>
        </label>
        <label className="hint">then{' '}
          <select aria-label={`${name} then`} data-testid={`ed-link-then-${name}`} value={thenList ? 'widgets' : String(link.then ?? '')} onChange={(e) => option('then', e.target.value === '' ? undefined : e.target.value === 'widgets' ? widgets.slice(0, 1) : e.target.value)}>
            <option value="">(default{isForm ? ': refresh' : ': none'})</option>
            <option value="refresh">refresh the source widget</option>
            <option value="none">nothing</option>
            <option value="widgets" disabled={widgets.length === 0}>these widgets</option>
          </select>
        </label>
        <button type="button" className="btn btn-sm" data-testid={`ed-link-remove-${name}`} onClick={() => apply({ type: 'removeLink', name })}>Remove</button>
      </div>
      {thenList && (
        <div className="row hint" style={{ gap: 8, flexWrap: 'wrap' }} data-testid={`ed-link-then-widgets-${name}`}>
          refresh:{' '}
          {widgets.map((w) => (
            <label key={w}><input type="checkbox" data-testid={`ed-link-then-widget-${name}-${w}`} checked={(link.then as string[]).includes(w)} onChange={(e) => {
              const now = new Set(link.then as string[])
              if (e.target.checked) now.add(w); else now.delete(w)
              option('then', now.size === 0 ? undefined : widgets.filter((x) => now.has(x)))
            }} /> {w}</label>
          ))}
        </div>
      )}
      <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap', marginTop: 4 }}>
        <label className="hint">PDF and print{' '}
          <select aria-label={`${name} print`} data-testid={`ed-link-print-${name}`} value={link.print ?? ''} onChange={(e) => option('print', e.target.value === '' ? undefined : e.target.value)}>
            <option value="">(by meaning: a value prints, an open arrow does not)</option>
            {LINK_PRINT.map((p) => <option key={p} value={p}>{p === 'link' ? 'link (a PDF hyperlink, the address is not printed)' : p === 'normal' ? 'normal (as on screen, no link)' : 'hidden'}</option>)}
          </select>
        </label>
        <label className="hint">image (PNG){' '}
          <select aria-label={`${name} image`} data-testid={`ed-link-image-${name}`} value={link.image ?? ''} onChange={(e) => option('image', e.target.value === '' ? undefined : e.target.value)}>
            <option value="">(as print)</option>
            {LINK_PRINT.map((p) => <option key={p} value={p}>{p}</option>)}
          </select>
        </label>
        <label className="hint">label <input aria-label={`${name} label`} data-testid={`ed-link-label-${name}`} style={{ width: 140 }} value={link.label ?? ''} onChange={(e) => option('label', e.target.value === '' ? undefined : e.target.value)} /></label>
      </div>
      <table className="table" style={{ marginTop: 4 }}>
        <tbody>
          {Object.entries(values).map(([k, v]) => (
            <tr key={k} data-testid={`ed-link-value-row-${name}-${k}`}>
              <td><code>{k}</code></td>
              <td><input aria-label={`${name} ${k}`} data-testid={`ed-link-value-${name}-${k}`} value={showLinkValue(v)} onChange={(e) => apply({ type: 'setLinkValue', name, param: k, value: parseLinkValue(e.target.value) }, `link:${name}:value:${k}`)} /></td>
              {isForm && <td><label className="hint"><input type="checkbox" data-testid={`ed-link-lock-${name}-${k}`} checked={locked.has(k)} onChange={(e) => apply({ type: 'setLinkLock', name, field: k, locked: e.target.checked })} /> read-only in the form</label></td>}
              <td><button type="button" className="btn btn-sm" data-testid={`ed-link-value-remove-${name}-${k}`} onClick={() => apply({ type: 'setLinkValue', name, param: k, value: undefined })}>Remove</button></td>
            </tr>
          ))}
          {Object.keys(values).length === 0 && <tr><td className="hint">Gives {isForm ? 'no fields' : 'no parameters'}.</td></tr>}
        </tbody>
      </table>
      <div className="row" style={{ gap: 6, alignItems: 'center' }}>
        <input aria-label={`${name} new value name`} data-testid={`ed-link-value-name-${name}`} placeholder={isForm ? 'field' : 'parameter'} value={param} onChange={(e) => setParam(e.target.value)} />
        <input aria-label={`${name} new value`} data-testid={`ed-link-value-new-${name}`} placeholder="$orders.id, $params.region or a value" value={value} onChange={(e) => setValue(e.target.value)} />
        <button type="button" className="btn btn-sm" data-testid={`ed-link-value-add-${name}`} disabled={!VALUE_NAME.test(param) || value.trim() === ''}
          onClick={() => { apply({ type: 'setLinkValue', name, param, value: parseLinkValue(value) }); setParam(''); setValue('') }}>Add value</button>
      </div>
      <Problems diagnostics={mine} testId={`ed-link-problems-${name}`} />
    </div>
  )
}

/**
 * The document's links (plan 32 DBR38): each link's target, the values it gives, how the shell presents it, what re-runs after it, whether a form shows a prefilled field read-only and what a PDF or PNG draws.
 * Every change is a command of the editor (`linkCommands`), so it is one undo step and the validator answers on it; a target the app lacks and a type that does not fit are the publish check's to say.
 */
export function LinkEditor({ doc, diagnostics, apply }: { doc: ViewsDoc; diagnostics: ClientDiagnostic[]; apply: Apply }) {
  const [name, setName] = useState('')
  const [kind, setKind] = useState<LinkTargetKind>('page')
  const [target, setTarget] = useState('')
  const links = (doc.links ?? {}) as Record<string, LinkDoc>
  const taken = name in links
  const canAdd = LINK_NAME.test(name) && !taken && NAME_PATTERN.test(target) && LINK_NAME.test(target)
  return (
    <div className="card" style={{ marginBottom: 12 }} data-testid="ed-links">
      <strong>Links</strong> <span className="hint">a chart, matrix or table follows one on a click (set it under Widget properties)</span>
      {Object.entries(links).map(([n, l]) => (l.change !== undefined || l.task !== undefined
        ? <ActionRow key={n} name={n} link={l} doc={doc} diagnostics={diagnostics} apply={apply} />
        : <LinkRow key={n} name={n} link={l} doc={doc} diagnostics={diagnostics} apply={apply} />))}
      {Object.keys(links).length === 0 && <p className="hint" data-testid="ed-links-none">No links.</p>}
      <div className="row" style={{ gap: 6, alignItems: 'center' }}>
        <input aria-label="New link name" data-testid="ed-link-name" placeholder="order-form" value={name} onChange={(e) => setName(e.target.value)} />
        <select aria-label="New link target kind" data-testid="ed-link-new-kind" value={kind} onChange={(e) => setKind(e.target.value as LinkTargetKind)}>
          {LINK_TARGETS.map((t) => <option key={t} value={t}>{t}</option>)}
        </select>
        <input aria-label="New link target" data-testid="ed-link-new-target" placeholder="name in the app" value={target} onChange={(e) => setTarget(e.target.value)} />
        <button type="button" className="btn btn-sm" data-testid="ed-link-add" disabled={!canAdd}
          onClick={() => { apply({ type: 'addLink', name, link: { [kind]: target } as LinkDoc }); setName(''); setTarget('') }}>Add link</button>
      </div>
      <ActionAdder links={links} apply={apply} />
    </div>
  )
}
