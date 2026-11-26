import { useEffect, useMemo, useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { useDraft, useProvisioningPlan } from '@dbdatakit/workspace'
import { syncApi, unsavedOf, type Unsaved } from './syncApi'

/** A short, stable digest of the edits a question carries, so an answer is asked again when they change and kept while they do not. */
export function digestOf(unsaved: Unsaved): string {
  const text = JSON.stringify(Object.entries(unsaved))
  let h = 5381
  for (let i = 0; i < text.length; i++) h = ((h << 5) + h + text.charCodeAt(i)) | 0
  return `${Object.keys(unsaved).length}:${(h >>> 0).toString(36)}`
}

/** The draft's unsaved edits a question carries now, and their digest. */
export function useUnsaved(): { unsaved: Unsaved; digest: string } {
  const draft = useDraft()
  const unsaved = unsavedOf(draft.dirtyPaths, draft.textOf)
  const digest = digestOf(unsaved)
  // kept by its digest: the same edits give the same object, so a query keyed by the digest reads the edits it was asked with
  return useMemo(() => ({ unsaved, digest }), [digest]) // oxlint-disable-line react-hooks/exhaustive-deps
}

const key = (project: string, id: string, ...rest: unknown[]) => ['workspace', 'sync-designer', project, id, ...rest]

/** The script slots a binding can name (the same for every draft of a server's version; asked once per draft). */
export function useScriptSlots() {
  const draft = useDraft()
  return useQuery({ queryKey: key(draft.project, draft.id, 'script-slots'), queryFn: () => syncApi.scriptSlots(draft.project, draft.id), staleTime: Infinity })
}

/** What a connection's driver offers, asked with the draft's unsaved edits; nothing is asked for no connection. */
export function useCapabilities(connection: string | null | undefined) {
  const draft = useDraft()
  const { unsaved, digest } = useUnsaved()
  return useQuery({
    queryKey: key(draft.project, draft.id, 'capabilities', connection ?? '', digest),
    queryFn: () => syncApi.capabilities(draft.project, draft.id, connection!, unsaved),
    enabled: !!connection,
    retry: false,
    staleTime: 60_000,
  })
}

/** The target types and natural key one mapping would get, asked with the draft's unsaved edits; nothing is asked for a mapping not named. */
export function useInferredColumns(replication: string, mapping: string | null | undefined, enabled = true) {
  const draft = useDraft()
  const { unsaved, digest } = useUnsaved()
  return useQuery({
    queryKey: key(draft.project, draft.id, 'inferred-columns', replication, mapping ?? '', digest),
    queryFn: () => syncApi.inferredColumns(draft.project, draft.id, replication, mapping!, unsaved),
    enabled: enabled && !!mapping,
    retry: false,
    staleTime: 60_000,
  })
}

/** The files Create mappings would make for the ticked tables, asked with the draft's unsaved edits; the caller puts them in the draft. */
export function useBulkCreate(replication: string) {
  const draft = useDraft()
  const { unsaved } = useUnsaved()
  return useMutation({ mutationFn: (tables: { schema: string; table: string }[]) => syncApi.bulkCreate(draft.project, draft.id, replication, tables, unsaved) })
}

/** How long the edits stay unchanged before a replication's plan is asked again: the plan runs a program, so typing does not ask it on each key. */
export const PLAN_SETTLE_MS = 300

/**
 * A replication's provisioning plan made with the draft's unsaved edits, so the Provisioning tabs and a mapping's count follow what the person
 * sees. The first plan is asked at once; a later edit is asked once the edits have stayed unchanged for {@link PLAN_SETTLE_MS}, and the plan
 * before it stays shown meanwhile.
 */
export function useDraftProvisioningPlan(replication: string) {
  const draft = useDraft()
  const edits = useUnsaved()
  const [settled, setSettled] = useState(edits)
  useEffect(() => {
    if (edits.digest === settled.digest) return
    const timer = setTimeout(() => setSettled(edits), PLAN_SETTLE_MS)
    return () => clearTimeout(timer)
  }, [edits, settled.digest])
  return useProvisioningPlan(draft.project, draft.id, replication, settled)
}
