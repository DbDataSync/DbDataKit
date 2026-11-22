import { useState } from 'react'
import { Field, Link } from '@dbdatakit/shell'
import { connectionsOf, filesPath, replicationPath, syncNameProblem, useDraft } from '@dbdatakit/workspace'
import { newTaskText, taskPath } from '../model'
import type { ScheduleMode } from '../types'

/** The pipeline a new replication starts with: kinds every driver offers with no options, to be reviewed on its Overview once the endpoints are chosen. */
export const NEW_PIPELINE = { reader: 'BatchReload', cache: 'StagingTable', writer: 'DeleteInsert' }

/**
 * A new replication: its name and schedule. It is made in the draft as an unsaved `task.yaml`, disabled until someone turns it on, with no
 * endpoints: the source and target are picked on its Overview. With no connection in the draft it says so and links to the connections in Files.
 */
export function NewReplicationForm({ onCreated, onCancel }: { onCreated: (name: string) => void; onCancel: () => void }) {
  const draft = useDraft()
  const [name, setName] = useState('')
  const [mode, setMode] = useState<ScheduleMode>('Continuous')
  const [frequencySeconds, setFrequencySeconds] = useState(60)
  const [cronExpression, setCronExpression] = useState('0 * * * *')
  const problem = syncNameProblem(name.trim(), draft.paths, replicationPath)
  const canCreate = connectionsOf(draft.paths).length > 0

  const create = (e: React.FormEvent) => {
    e.preventDefault()
    const n = name.trim()
    if (!canCreate || n === '' || problem) return
    draft.edit(taskPath(n), newTaskText(n, mode === 'Continuous' ? { mode, frequencySeconds } : { mode, cronExpression }))
    onCreated(n)
  }

  return (
    <div className="card" data-testid="new-replication-form">
      <div className="card-head"><span className="card-title">New replication</span></div>
      <div className="card-body">
        <form onSubmit={create} className="stack">
          <Field label="Name">
            <input className="input" required value={name} onChange={(e) => setName(e.target.value)} data-testid="replication-name-input" />
          </Field>
          {problem && <span className="field-error" data-testid="replication-name-problem">{problem}</span>}

          <div className="form-grid">
            <Field label="Schedule mode">
              <select className="select" value={mode} onChange={(e) => setMode(e.target.value as ScheduleMode)} data-testid="new-replication-mode">
                <option value="Continuous">Continuous</option>
                <option value="Periodic">Periodic (cron)</option>
              </select>
            </Field>
            {mode === 'Continuous' ? (
              <Field label="Frequency (seconds)">
                <input className="input" type="number" min={1} value={frequencySeconds} onChange={(e) => setFrequencySeconds(Number(e.target.value))} data-testid="new-replication-frequency" />
              </Field>
            ) : (
              <Field label="Cron expression">
                <input className="input" value={cronExpression} onChange={(e) => setCronExpression(e.target.value)} data-testid="new-replication-cron" />
              </Field>
            )}
          </div>

          {canCreate ? (
            <span className="hint">
              The pipeline starts as <span className="mono">{NEW_PIPELINE.reader}</span> → <span className="mono">{NEW_PIPELINE.cache}</span> →{' '}
              <span className="mono">{NEW_PIPELINE.writer}</span>, to be reviewed once a source and target are chosen on the replication's Overview.
            </span>
          ) : (
            <p className="field-error" data-testid="no-connection-warning">
              This draft has no connection yet: a replication needs one to point its source and target at.{' '}
              <Link to={filesPath(draft.project, draft.id, 'config/connections')}>Add one in Files</Link>.
            </p>
          )}

          <div className="row">
            <button type="submit" className="btn btn-primary" disabled={!canCreate || name.trim() === '' || !!problem} data-testid="create-replication-button">Create</button>
            <button type="button" className="btn" onClick={onCancel} data-testid="cancel-replication-button">Cancel</button>
          </div>
        </form>
      </div>
    </div>
  )
}
