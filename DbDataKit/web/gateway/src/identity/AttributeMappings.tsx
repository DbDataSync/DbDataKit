import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { identityApi, type AttributeMapping } from './api'
import { checkMapping, mappingRequest, ordered, sentence, summarise, type MappingForm } from './attributeMappings'

export interface AttributeMappingsViewProps {
  rows: AttributeMapping[] | undefined
  loading: boolean
  /** Why the list could not be read, in words, or null. */
  loadError: string | null
  form: MappingForm
  busy: boolean
  /** The server's refusal of the last change, in its words, or null. */
  refusal: string | null
  onForm: (form: MappingForm) => void
  onAdd: () => void
  onRemove: (id: string) => void
}

/** The attribute mappings of the identity home (plan 33, UE15): a directory group (Entra or Windows) gives its members a value of an attribute, which reaches their tokens and decides what they see. Draws what it is given. */
export function AttributeMappingsView(p: AttributeMappingsViewProps) {
  const rows = ordered(p.rows ?? [])
  const check = checkMapping(p.form, rows)
  const summary = summarise(rows)
  return (
    <div className="card flush" data-testid="identity-attribute-mappings">
      {p.loading && <div className="empty" role="status" data-testid="identity-attribute-loading">Reading the attribute mappings…</div>}
      {p.loadError && <div className="empty" role="alert" data-testid="identity-attribute-error">The attribute mappings could not be read: {p.loadError}</div>}
      {!p.loading && !p.loadError && rows.length === 0 && <div className="empty" data-testid="identity-attribute-empty">No group gives an attribute. A mapping gives everyone in a directory group a value (region = east), and what a person may see can follow it.</div>}
      {rows.map((m) => (
        <div key={m.id} className="grid-row" style={{ gridTemplateColumns: '1fr 3fr 2fr 2fr 1fr', gap: 14 }} data-testid={`identity-attribute-${m.attribute}-${m.value}`}>
          <span>{m.source}</span><span className="mono">{m.group}</span><span className="mono">{m.attribute}</span><span className="mono">{m.value}</span>
          <button type="button" className="btn" disabled={p.busy} title={sentence(m)} onClick={() => p.onRemove(m.id)} data-testid="identity-attribute-remove">Remove</button>
        </div>
      ))}
      {summary.length > 0 && (
        <div className="hint" style={{ padding: '8px 14px' }} data-testid="identity-attribute-summary">
          {summary.map((s) => <span key={s.attribute} style={{ marginRight: 16 }} data-testid={`identity-attribute-summary-${s.attribute}`}>{s.attribute}: {s.values.join(', ')}</span>)}
        </div>
      )}
      <form className="grid-row" style={{ gridTemplateColumns: '1fr 3fr 2fr 2fr 1fr', gap: 14 }} data-testid="identity-attribute-form" onSubmit={(e) => { e.preventDefault(); if (check.ok) p.onAdd() }}>
        <select className="input" aria-label="Directory" value={p.form.source} onChange={(e) => p.onForm({ ...p.form, source: e.target.value })} data-testid="identity-attribute-source"><option value="entra">entra</option><option value="windows">windows</option></select>
        <input className="input" aria-label="Group" placeholder="group id or DOMAIN\group" value={p.form.group} onChange={(e) => p.onForm({ ...p.form, group: e.target.value })} data-testid="identity-attribute-group" />
        <input className="input" aria-label="Attribute" placeholder="attribute, e.g. region" value={p.form.attribute} aria-invalid={check.problems.attribute ? true : undefined} onChange={(e) => p.onForm({ ...p.form, attribute: e.target.value })} data-testid="identity-attribute-name" />
        <input className="input" aria-label="Value" placeholder="value, e.g. east" value={p.form.value} aria-invalid={check.problems.value ? true : undefined} onChange={(e) => p.onForm({ ...p.form, value: e.target.value })} data-testid="identity-attribute-value" />
        <button type="submit" className="btn" disabled={p.busy || !check.ok} title={check.why ?? 'Give the group\'s members this value'} data-testid="identity-attribute-add">Map</button>
      </form>
      {(check.problems.attribute || check.problems.value) && <p className="hint" role="alert" style={{ padding: '0 14px' }} data-testid="identity-attribute-problem">{check.problems.attribute ?? check.problems.value}</p>}
      {check.why && <p className="hint" style={{ padding: '0 14px 8px' }} data-testid="identity-attribute-why">{check.why}</p>}
      {p.refusal && <p className="hint" role="alert" style={{ padding: '0 14px 8px' }} data-testid="identity-attribute-refused">{p.refusal}</p>}
    </div>
  )
}

const blank: MappingForm = { source: 'entra', group: '', attribute: '', value: '' }

/** The card of the identity-home screen: reads, adds and removes; the identity home checks the role and the rules again. */
export function AttributeMappingsCard() {
  const client = useQueryClient()
  const key = ['identity-home', 'attribute-mappings']
  const list = useQuery({ queryKey: key, queryFn: identityApi.attributeMappings.list, retry: false })
  const [form, setForm] = useState<MappingForm>(blank)
  const change = useMutation({
    mutationFn: (action: () => Promise<unknown>) => action(),
    onSuccess: () => client.invalidateQueries({ queryKey: ['identity-home'] }),
  })
  const failure = change.error ? (change.error instanceof Error ? change.error.message : String(change.error)) : null
  return (
    <>
      <AttributeMappingsView
        rows={list.data} loading={list.isPending} loadError={list.error ? (list.error instanceof Error ? list.error.message : String(list.error)) : null}
        form={form} busy={change.isPending} refusal={failure} onForm={setForm}
        onAdd={() => change.mutate(async () => { await identityApi.attributeMappings.add(mappingRequest(form)); setForm({ ...blank, source: form.source }) })}
        onRemove={(id) => change.mutate(() => identityApi.attributeMappings.remove(id))}
      />
    </>
  )
}
