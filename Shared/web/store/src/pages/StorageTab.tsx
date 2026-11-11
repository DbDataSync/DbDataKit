import { useState } from 'react'
import { useQueries, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, ErrorBanner } from '@dbdatakit/shell'
import { storeApi, type MigrationPlan } from '../api'
import { errorsOf, storageReason } from '../model'
import { CopyView } from '../views/StorageView'

/** The storage of a model: each copy (its own, and each instance that keeps tables of its own) with what is in the database, create the tables, and migrate them by adding only (a dry run first). */
export function StorageTab({ model }: { model: string }) {
  const queryClient = useQueryClient()
  const doc = useQuery({ queryKey: ['store', 'model', model], queryFn: () => storeApi.model(model) })
  const instances = useQuery({ queryKey: ['store', 'instances', model], queryFn: () => storeApi.instances(model) })
  const migrations = useQuery({ queryKey: ['store', 'migrations', model], queryFn: () => storeApi.migrations(model) })
  // a model that shares its tables (column) has one set of them: its own copy
  const copies = [{ name: 'default', connection: null as string | null, schema: null as string | null }, ...(instances.data?.mode === 'column' ? [] : instances.data?.instances ?? [])]
  const plans = useQueries({ queries: copies.map((c) => ({ queryKey: ['store', 'storage', model, c.name], queryFn: () => storeApi.storage(model, c.name), retry: false })) })
  const [dry, setDry] = useState<Record<string, MigrationPlan | null>>({})
  const [done, setDone] = useState<Record<string, string | null>>({})
  const [refusal, setRefusal] = useState<Record<string, string | null>>({})
  const [busy, setBusy] = useState<string | null>(null)

  const valid = doc.data ? errorsOf(doc.data.diagnostics).length === 0 : false
  const blocked = doc.data ? storageReason(doc.data.can.admin, valid, false) : 'Loading...'

  const run = async (copy: string, work: () => Promise<string | null>) => {
    setBusy(copy); setRefusal((r) => ({ ...r, [copy]: null })); setDone((d) => ({ ...d, [copy]: null }))
    try {
      const said = await work()
      setDone((d) => ({ ...d, [copy]: said }))
      await queryClient.invalidateQueries({ queryKey: ['store', 'storage', model] })
      await queryClient.invalidateQueries({ queryKey: ['store', 'migrations', model] })
    } catch (e) {
      // a migration that cannot be applied answers 422 with the list of what blocks it: show it as a dry run does
      if (e instanceof ApiError && (e.body as { blocked?: string[] } | undefined)?.blocked) setDry((d) => ({ ...d, [copy]: e.body as MigrationPlan }))
      setRefusal((r) => ({ ...r, [copy]: e instanceof Error ? e.message : String(e) }))
    } finally { setBusy(null) }
  }

  return (
    <div data-testid="store-storage">
      <p className="hint">The tables of a model are made on the connection its <code>storage</code> names. Creating them only creates; a newer version of the model is migrated by adding tables and nullable columns, and anything more is left for a planned change.</p>
      <ErrorBanner error={doc.error ?? instances.error ?? migrations.error} />
      {instances.data && instances.data.mode !== 'none' && <p className="hint" data-testid="storage-mode">Instances are kept apart by {instances.data.mode}.</p>}
      {copies.map((c, i) => {
        const plan = plans[i]!
        const state = migrations.data?.instances.find((s) => s.name === c.name)
        return (
          <CopyView
            key={c.name} name={c.name} schema={c.schema} connection={c.connection}
            plan={plan.data} planError={plan.error instanceof Error ? plan.error.message : null} state={state}
            dry={dry[c.name] ?? null} done={done[c.name] ?? null} refusal={refusal[c.name] ?? null}
            blocked={blocked} busy={busy !== null}
            onCreate={() => run(c.name, async () => { const r = await storeApi.createStorage(model, c.name); setDry((d) => ({ ...d, [c.name]: null })); return `Created: ${r.statements.length} statement${r.statements.length === 1 ? '' : 's'} on ${r.engine}.` })}
            onDryRun={() => run(c.name, async () => { setDry((d) => ({ ...d, [c.name]: null })); const r = await storeApi.migrate(model, c.name, false); setDry((d) => ({ ...d, [c.name]: r })); return null })}
            onApply={() => run(c.name, async () => { const r = await storeApi.migrate(model, c.name, true); setDry((d) => ({ ...d, [c.name]: r })); return r.applied ? 'Migrated: the tables now match the model.' : null })}
          />
        )
      })}
    </div>
  )
}
