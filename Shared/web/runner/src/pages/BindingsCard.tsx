import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'
import { runnerApi, type BindRequest, type DefinitionDetail } from '../api'
import { bindingBlock, bindingsRequest, connectionNames, slotRows, unboundSlots, type BindingEdits, type SlotRow } from '../bindings'

export interface BindingsCardViewProps {
  channel: string
  rows: SlotRow[]
  /** Whether the person is offered the editor (the admin role; the runner checks again). */
  canEdit: boolean
  edits: BindingEdits
  /** The connection names the runner knows, offered while typing. */
  known: string[]
  busy: boolean
  /** The slots the last save bound, shown until the next try. */
  saved: string[] | null
  /** The last call that was refused or failed. */
  failure: unknown
  onEdit: (slot: string, value: string) => void
  onSave: () => void
}

/** The connections of one channel without their calls: each slot the manifests declare, what it is bound to, and the change. Hook-free so a test can draw it and press its button. */
export function BindingsCardView(p: BindingsCardViewProps) {
  const block = bindingBlock(p.canEdit, p.rows, p.edits)
  const unbound = unboundSlots(p.rows)
  const listId = `runner-known-connections-${p.channel}`
  return (
    <div style={{ marginTop: 12 }} data-testid="runner-bindings" data-channel={p.channel}>
      <div className="card-title">Connections in {p.channel}</div>
      <div className="hint">A slot is bound to the name of a connection the runner has (or a secret reference), never to a login. Each channel binds its own.</div>
      {p.rows.length === 0 && <div className="empty" data-testid="runner-bindings-empty">No version served or staged in {p.channel} declares a connection slot.</div>}
      {unbound.length > 0 && <div className="field-error" role="alert" data-testid="runner-bindings-unbound">Not bound yet: {unbound.join(', ')}. A version that declares a slot cannot run in this channel until it is bound.</div>}
      {p.rows.length > 0 && (
        <div className="card flush">
          <div className="grid-head" style={{ gridTemplateColumns: '1fr 0.8fr 1.6fr 2fr', gap: 14 }}><span>Slot</span><span>Kind</span><span>Used by</span><span>Bound to</span></div>
          {p.rows.map((r) => {
            const typed = p.edits[r.slot]
            return (
              <div key={r.slot} className="grid-row" style={{ gridTemplateColumns: '1fr 0.8fr 1.6fr 2fr', gap: 14 }} data-testid="runner-binding-row" data-slot={r.slot} data-bound={r.boundTo ?? ''} data-revision={r.revision ?? ''} data-declared={r.declared}>
                <span className="mono">{r.slot}</span>
                <span>{r.kind ?? ''}</span>
                <span className="hint">{r.declared ? `${r.tasks.join(', ')} (v${r.versions.join(', v')})` : 'no version declares it now'}</span>
                {p.canEdit
                  ? <input className="input mono" list={listId} value={typed ?? r.boundTo ?? ''} placeholder="connection name" aria-label={`Connection for ${r.slot}`} onChange={(e) => p.onEdit(r.slot, e.target.value)} data-testid="runner-binding-input" />
                  : <span className="mono" data-testid="runner-binding-value">{r.boundTo ?? 'not bound'}</span>}
              </div>
            )
          })}
        </div>
      )}
      <datalist id={listId} data-testid="runner-known-connections">{p.known.map((n) => <option key={n} value={n} />)}</datalist>
      <ErrorBanner error={p.failure} />
      {p.saved && <div className="hint" data-testid="runner-bindings-saved">Bound in {p.channel}: {p.saved.join(', ')}.</div>}
      {p.rows.length > 0 && (
        <div className="row" style={{ gap: 12, marginTop: 8 }}>
          <button type="button" className="btn btn-primary" disabled={block !== null || p.busy} onClick={p.onSave} data-testid="runner-bindings-save">{p.busy ? 'Saving...' : 'Save connections'}</button>
          {block !== null && !p.busy && <span className="hint" data-testid="runner-bindings-why">{block}</span>}
        </div>
      )}
    </div>
  )
}

/** The connections of a channel of a package on the runner (plan 33, UE08): the slots from the manifests, bound by an administrator, audited by the runner. */
export function BindingsCard({ detail, channel, canEdit }: { detail: DefinitionDetail; channel: string; canEdit: boolean }) {
  const client = useQueryClient()
  const [edits, setEdits] = useState<BindingEdits>({})
  const [saved, setSaved] = useState<string[] | null>(null)
  const rows = slotRows(detail, channel)
  // the names the runner's channels bind already; a runner too old to answer leaves the list out
  const connections = useQuery({ queryKey: ['runner', 'connections'], queryFn: runnerApi.connections, retry: false, enabled: canEdit, refetchOnMount: 'always' })
  const save = useMutation({
    mutationFn: (request: BindRequest) => runnerApi.bind(detail.name, channel, request),
    onSuccess: (_answer, request) => {
      setSaved(Object.keys(request.slots))
      setEdits({})
      void client.invalidateQueries({ queryKey: ['runner'] })
    },
    // a refusal usually means the channel changed since the page was read (a revision moved): read it again so the next try carries the revision it is now at, and keep what was typed
    onError: () => { void client.invalidateQueries({ queryKey: ['runner'] }) },
  })
  return (
    <BindingsCardView
      channel={channel} rows={rows} canEdit={canEdit} edits={edits} known={connectionNames(connections.data)} busy={save.isPending} saved={saved} failure={save.error}
      onEdit={(slot, value) => { save.reset(); setSaved(null); setEdits({ ...edits, [slot]: value }) }}
      onSave={() => { const request = bindingsRequest(rows, edits); if (request) save.mutate(request) }}
    />
  )
}
