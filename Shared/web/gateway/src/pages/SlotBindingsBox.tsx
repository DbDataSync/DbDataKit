import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner, useIsAdmin } from '@dbdatakit/shell'
import { gatewayApi } from '../api'
import type { DefinitionBindingRow, DefinitionDescription } from '../types'

/** One slot of a version a channel serves, and what the channel binds it to (plan 33 SW02). */
export interface SlotRow { channel: string; version: number; slot: string; boundTo: string | null; boundBy: string | null; boundAt: string | null; revision: number | null }

/** The slots a version's manifest names (`slots`, plan 33 SW01b: the data sources a model needs that the publisher did not make); none when the manifest is not JSON or names none. */
export function slotsOfManifest(manifest: string | null | undefined): string[] {
  if (!manifest) return []
  try {
    const parsed = JSON.parse(manifest) as { slots?: unknown }
    return Array.isArray(parsed.slots) ? parsed.slots.filter((s): s is string => typeof s === 'string') : []
  } catch { return [] }
}

/** For each channel that serves a version: each slot that version names, with the channel's binding of it (or none). */
export function slotRows(description: DefinitionDescription, slotsOfVersion: Record<number, string[]>): SlotRow[] {
  const rows: SlotRow[] = []
  for (const head of description.channels) {
    if (head.active == null) continue
    for (const slot of slotsOfVersion[head.active] ?? []) {
      const bound: DefinitionBindingRow | undefined = description.bindings.find((b) => b.channel === head.channel && b.slot === slot)
      rows.push({ channel: head.channel, version: head.active, slot, boundTo: bound?.bound_to ?? null, boundBy: bound?.bound_by ?? null, boundAt: bound?.bound_at ?? null, revision: bound?.revision ?? null })
    }
  }
  return rows
}

/**
 * The slot bindings of a published model (plan 33 SW02, the editor of SW01b): for each channel, the data sources the served version names as slots, what each is bound to, who bound it and when. An administrator binds a slot to the
 * data source of the same name (a data source made by hand, which a publisher may not bind) and removes a binding; after that a publish over that data source needs no confirmation (adopt). An administrator's box: the Gateway lets only an administrator or a publisher read a model's definition.
 */
export function SlotBindingsBox({ kind, name }: { kind: 'semantic-model' | 'entity-model'; name: string }) {
  const isAdmin = useIsAdmin()
  const queryClient = useQueryClient()
  const [notice, setNotice] = useState<string | null>(null)
  const description = useQuery({ queryKey: ['gateway', 'definitions', kind, name], queryFn: () => gatewayApi.definitions.read(kind, name), enabled: isAdmin })
  const versions = [...new Set((description.data?.channels ?? []).map((c) => c.active).filter((v): v is number => v != null))]
  const manifests = useQuery({
    queryKey: ['gateway', 'definitions', kind, name, 'slots', versions.join(',')],
    enabled: isAdmin && versions.length > 0,
    queryFn: async () => Object.fromEntries(await Promise.all(versions.map(async (v) => [v, slotsOfManifest((await gatewayApi.definitions.version(kind, name, v)).manifest)] as const))) as Record<number, string[]>,
  })
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['gateway', 'definitions', kind, name] })
  const bind = useMutation({
    mutationFn: (row: SlotRow) => gatewayApi.definitions.bind(row.channel, kind, name, { [row.slot]: row.slot }, row.revision ?? undefined),
    onSuccess: (_, row) => { setNotice(`Bound ${row.slot} to the data source ${row.slot} in ${row.channel}.`); return refresh() },
  })
  const unbind = useMutation({
    mutationFn: (row: SlotRow) => gatewayApi.definitions.unbind(row.channel, kind, name, row.slot),
    onSuccess: (_, row) => { setNotice(`Removed the binding of ${row.slot} in ${row.channel}.`); return refresh() },
  })
  const rows = description.data && manifests.data ? slotRows(description.data, manifests.data) : []
  if (!isAdmin || description.isLoading || manifests.isLoading) return null
  return (
    <div className="card" style={{ margin: '8px 0' }} data-testid="slot-bindings" data-kind={kind} data-name={name}>
      <strong>Data sources it needs (slots)</strong>
      <p className="hint">A data source the publisher did not make is a slot of the version: the Gateway serves the version once the channel binds the slot. An administrator binds it once, here; a publish over it then needs no confirmation.</p>
      {rows.length === 0 && <div className="hint" data-testid="slot-bindings-none">The version served names no slot: every data source it reads is the publisher's own.</div>}
      {rows.map((r) => (
        <div key={`${r.channel}/${r.slot}`} className="grid-row" style={{ gap: 8, alignItems: 'center' }} data-testid="slot-binding" data-slot={r.slot} data-channel={r.channel} data-bound={r.boundTo ?? ''}>
          <span className="mono">{r.slot}</span>
          <span className="hint">{r.channel}, v{r.version}</span>
          {r.boundTo
            ? <span>bound to <span className="mono">{r.boundTo}</span>{r.boundBy ? ` by ${r.boundBy}` : ''}{r.boundAt ? ` on ${r.boundAt.slice(0, 16).replace('T', ' ')}` : ''}</span>
            : <span className="error">not bound: the version is refused here until it is</span>}
          {isAdmin && !r.boundTo && <button type="button" className="btn btn-sm" disabled={bind.isPending} onClick={() => bind.mutate(r)} data-testid="slot-bind">Bind to the data source {r.slot}</button>}
          {isAdmin && r.boundTo && <button type="button" className="btn btn-sm" disabled={unbind.isPending} onClick={() => unbind.mutate(r)} data-testid="slot-unbind">Remove the binding</button>}
        </div>
      ))}
      {notice && <div className="hint" data-testid="slot-bindings-notice">{notice}</div>}
      <ErrorBanner error={description.error ?? manifests.error ?? bind.error ?? unbind.error} />
    </div>
  )
}
