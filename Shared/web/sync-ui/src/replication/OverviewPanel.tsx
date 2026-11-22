import { EndpointsCard } from './EndpointsCard'
import { ScheduleCard } from './ScheduleCard'
import type { ConnectionView, ReplicationTaskConfig } from '../types'

/**
 * A replication's Overview: the source and target on top, then the schedule. The endpoints stay above everything else because every setting
 * below is about what happens between those two. The Overview's sub-tabs (Notes, Pipeline, Provisioning, Custom Transforms, Bulk Load) and the
 * tracking settings are not drawn here yet; their keys are edited in Files meanwhile.
 */
export function OverviewPanel({ draft, setDraft, connections, mappingCount }: {
  draft: ReplicationTaskConfig
  setDraft: (next: ReplicationTaskConfig) => void
  connections: ConnectionView[]
  mappingCount: number
}) {
  return (
    <div className="pane" data-testid="replication-overview">
      <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
        <EndpointsCard
          endpoints={draft.endpoints}
          connections={connections}
          mappingCount={mappingCount}
          onChange={(endpoints) => setDraft({ ...draft, endpoints })}
        />
        <ScheduleCard draft={draft} onChange={setDraft} />
      </div>
    </div>
  )
}
