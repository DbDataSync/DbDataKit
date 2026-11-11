import { useState } from 'react'
import { LINK_AS, LINK_PRINT, LINK_TARGETS, NAME_PATTERN, type ClientDiagnostic, type EditCommand, type LinkDoc, type LinkTargetKind, type ViewsDoc } from '@dbdatakit/visuals'

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
      {Object.entries(links).map(([n, l]) => <LinkRow key={n} name={n} link={l} doc={doc} diagnostics={diagnostics} apply={apply} />)}
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
    </div>
  )
}
