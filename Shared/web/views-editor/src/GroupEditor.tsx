import { useState } from 'react'
import { NAME_PATTERN, groupProblems, groupUses, type Catalog, type EditCommand, type ViewsDoc } from '@dbdatakit/visuals'

type Apply = (command: EditCommand, group?: string) => void
type GroupDoc = NonNullable<ViewsDoc['groups']>[string]

const GROUP_NAME = /^[a-z][a-z0-9_-]{0,63}$/
const LEVEL_DATE = ['year', 'quarter', 'month', 'month-year', 'date'] as const
const levelField = (l: GroupDoc['fields'][number]): string => (typeof l === 'string' ? l : l.field)
const levelFormat = (l: GroupDoc['fields'][number]): unknown => (typeof l === 'string' ? undefined : l.format)

function GroupRow({ name, group, doc, catalog, apply }: { name: string; group: GroupDoc; doc: ViewsDoc; catalog: Catalog | null; apply: Apply }) {
  const [typed, setTyped] = useState('')
  const fields = group.fields
  const uses = groupUses(doc, name)
  const problems = groupProblems(name, group)
  const setFields = (next: GroupDoc['fields']) => apply({ type: 'setPath', path: ['groups', name, 'fields'], value: next }, `group:${name}:fields`)
  const setKey = (key: 'label' | 'template', value: string) => apply(value === '' ? { type: 'removePath', path: ['groups', name, key] } : { type: 'setPath', path: ['groups', name, key], value }, `group:${name}:${key}`)
  const setFormat = (i: number, preset: string) => {
    const field = levelField(fields[i]!)
    setFields(fields.map((l, j) => (j !== i ? l : preset === '' ? field : { field, format: { date: preset } })))
  }
  const attributes = (catalog?.attributes ?? []).filter((m) => !fields.some((l) => levelField(l) === m.name))
  const add = (field: string) => { if (field.trim() !== '' && fields.length < 4) { setFields([...fields, field.trim()]); setTyped('') } }
  return (
    <div className="card" style={{ margin: '6px 0' }} data-testid={`ed-group-${name}`}>
      <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
        <code style={{ minWidth: 90 }}>{name}</code>
        <label className="hint">label <input aria-label={`${name} label`} data-testid={`ed-group-label-${name}`} style={{ width: 120 }} value={group.label ?? ''} onChange={(e) => setKey('label', e.target.value)} /></label>
        <label className="hint">template <input aria-label={`${name} template`} data-testid={`ed-group-template-${name}`} style={{ width: 150 }} placeholder="{2} {1}" value={group.template ?? ''} onChange={(e) => setKey('template', e.target.value)} /></label>
        <button type="button" className="btn btn-sm" data-testid={`ed-group-remove-${name}`} disabled={uses.length > 0} title={uses.length > 0 ? `In use by ${uses.join(', ')}` : undefined}
          onClick={() => apply({ type: 'removePath', path: Object.keys(doc.groups ?? {}).length === 1 ? ['groups'] : ['groups', name] })}>Remove</button>
        {uses.length > 0 && <span className="hint" data-testid={`ed-group-uses-${name}`}>used by {uses.join(', ')}</span>}
      </div>
      <ol style={{ margin: '4px 0', paddingLeft: 20 }}>
        {fields.map((l, i) => (
          <li key={`${levelField(l)}-${i}`} data-testid={`ed-group-field-${name}-${i}`} className="row" style={{ gap: 6, alignItems: 'center', flexWrap: 'wrap' }}>
            <code>{levelField(l)}</code>
            <select aria-label={`Date format of ${levelField(l)}`} data-testid={`ed-group-format-${name}-${i}`} value={(levelFormat(l) as { date?: string } | undefined)?.date ?? ''} onChange={(e) => setFormat(i, e.target.value)}>
              <option value="">(as it is)</option>
              {LEVEL_DATE.map((d) => <option key={d} value={d}>{d}</option>)}
            </select>
            <button type="button" className="btn btn-sm" data-testid={`ed-group-up-${name}-${i}`} disabled={i === 0} onClick={() => { const next = [...fields]; const [m] = next.splice(i, 1); next.splice(i - 1, 0, m!); setFields(next) }}>Up</button>
            <button type="button" className="btn btn-sm" data-testid={`ed-group-down-${name}-${i}`} disabled={i === fields.length - 1} onClick={() => { const next = [...fields]; const [m] = next.splice(i, 1); next.splice(i + 1, 0, m!); setFields(next) }}>Down</button>
            <button type="button" className="btn btn-sm" data-testid={`ed-group-field-remove-${name}-${i}`} disabled={fields.length <= 2} onClick={() => setFields(fields.filter((_, j) => j !== i))}>Remove</button>
          </li>
        ))}
      </ol>
      <div className="row" style={{ gap: 6, alignItems: 'center', flexWrap: 'wrap' }}>
        {catalog && (
          <select aria-label={`Add an attribute to ${name}`} data-testid={`ed-group-pick-${name}`} value="" disabled={fields.length >= 4} onChange={(e) => { if (e.target.value) add(e.target.value) }}>
            <option value="">Add an attribute…</option>
            {attributes.map((m) => <option key={m.name} value={m.name}>{m.label ? `${m.label} (${m.name})` : m.name}</option>)}
          </select>
        )}
        <input aria-label={`Type an attribute for ${name}`} data-testid={`ed-group-type-${name}`} placeholder="or type a name" style={{ width: 150 }} value={typed} disabled={fields.length >= 4} onChange={(e) => setTyped(e.target.value)} onKeyDown={(e) => { if (e.key === 'Enter') add(typed) }} />
        {fields.length >= 4 && <span className="hint">a group has at most four levels</span>}
      </div>
      {problems.length > 0 && <ul className="hint" data-testid={`ed-group-problems-${name}`} style={{ margin: '2px 0' }}>{problems.map((p, i) => <li key={i} role="alert">{p}</li>)}</ul>}
    </div>
  )
}

/**
 * The document's field groups (plan 32 DBR52, DBR54): each group's levels in order (a level's date format, up, down, remove), its label and template, and what the loaders say of it as it is typed. A group is **named** by a chart channel
 * or a matrix axis in the widget's wells (the pick list offers the document's groups). Every change is a `setPath` of the document, so it is one undo step; a group a widget uses is not removed.
 */
export function GroupEditor({ doc, catalog, apply }: { doc: ViewsDoc; catalog: Catalog | null; apply: Apply }) {
  const [name, setName] = useState('')
  const [fields, setFields] = useState('')
  const groups = doc.groups ?? {}
  const list = fields.split(',').map((f) => f.trim()).filter(Boolean)
  const canAdd = GROUP_NAME.test(name) && NAME_PATTERN.test(name) && !(name in groups) && list.length >= 2 && list.length <= 4 && new Set(list).size === list.length
  return (
    <div className="card" style={{ marginBottom: 12 }} data-testid="ed-groups">
      <strong>Field groups</strong> <span className="hint">attributes used as one field: a chart&apos;s x, color, column or row, a matrix&apos;s rows or columns (pick a group in the widget&apos;s wells)</span>
      {Object.entries(groups).map(([n, g]) => <GroupRow key={n} name={n} group={g} doc={doc} catalog={catalog} apply={apply} />)}
      {Object.keys(groups).length === 0 && <p className="hint" data-testid="ed-groups-none">No groups.</p>}
      <div className="row" style={{ gap: 6, alignItems: 'center', flexWrap: 'wrap' }}>
        <input aria-label="New group name" data-testid="ed-group-name" placeholder="period" value={name} onChange={(e) => setName(e.target.value)} />
        <input aria-label="New group attributes" data-testid="ed-group-new-fields" style={{ width: 280 }} placeholder="orders.year, orders.month (2 to 4, outermost first)" value={fields} onChange={(e) => setFields(e.target.value)} />
        <button type="button" className="btn btn-sm" data-testid="ed-group-add" disabled={!canAdd}
          onClick={() => { apply({ type: 'setPath', path: ['groups', name], value: { fields: list } }); setName(''); setFields('') }}>Add group</button>
      </div>
    </div>
  )
}
