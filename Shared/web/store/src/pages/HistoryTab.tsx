import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { DiffViewer, ErrorBanner } from '@dbdatakit/shell'
import { refusalOf, storeApi } from '../api'
import { defaultVersion, restoreState } from '../model'
import { HistoryView } from '../views/HistoryView'

/** The history of a model's text (UI07): the earlier texts with who and when, a diff of one against the model now, and Restore as a new save quoting the version the model holds. */
export function HistoryTab({ model }: { model: string }) {
  const queryClient = useQueryClient()
  const history = useQuery({ queryKey: ['store', 'history', model], queryFn: () => storeApi.history(model) })
  const doc = useQuery({ queryKey: ['store', 'model-now', model], queryFn: () => storeApi.model(model), retry: false })
  const [chosen, setChosen] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [refusal, setRefusal] = useState<string | null>(null)
  const [done, setDone] = useState<string | null>(null)
  const versions = history.data?.versions ?? []
  const selected = chosen ?? defaultVersion(versions)
  const earlier = useQuery({ queryKey: ['store', 'history-text', model, selected], queryFn: () => storeApi.historyText(model, selected!), enabled: selected !== null })
  const exists = history.data?.exists ?? false
  const isCurrent = selected !== null && selected === history.data?.current

  const restore = async () => {
    if (!earlier.data || !doc.data) return
    setSaving(true); setRefusal(null); setDone(null)
    try {
      // the same save the editor makes, quoting the version read: a model someone changed since is a conflict, not an overwrite
      const saved = await storeApi.save(model, earlier.data.text, doc.data.version)
      setDone(`Restored: the model is now version ${saved.version.slice(0, 8)}.`)
      setChosen(null)
      await queryClient.invalidateQueries({ queryKey: ['store'] })
    } catch (e) {
      const r = refusalOf(e)
      setRefusal(r.status === 409 ? 'The model was changed by someone else since this page was read; the list is reloaded, choose again.' : r.message)
      if (r.status === 409) await queryClient.invalidateQueries({ queryKey: ['store'] })
    } finally { setSaving(false) }
  }

  if (history.isLoading) return <div className="empty" data-testid="history-loading">Loading...</div>
  if (history.error || !history.data) return <ErrorBanner error={history.error ?? new Error(`There is no model '${model}'.`)} />
  return (
    <HistoryView
      versions={versions} selected={selected} onSelect={(v) => { setChosen(v); setDone(null); setRefusal(null) }}
      diff={<DiffViewer before={earlier.data?.text ?? null} after={doc.data?.text ?? null} language="yaml" height={300} testId="history-diff-viewer" />}
      restore={restoreState({ exists, canSave: doc.data?.can.save ?? false, isCurrent, busy: saving })} onRestore={restore}
      done={done} refusal={refusal} removed={!exists}
    />
  )
}
