import { Link, NotesPanel, SubTabs, type SubTab } from '@dbdatakit/shell'
import { designerPath } from '@dbdatakit/workspace'
import { ScriptBindingsCard } from '../components/ScriptBindings'
import { EndpointsCard } from '../replication/EndpointsCard'
import { PipelineCard } from '../replication/PipelineCard'
import { ProvisioningTab } from '../replication/ProvisioningTab'
import { ReconcileConfigCard, reconcileOf } from '../replication/ReconcileConfigCard'
import { ScheduleCard } from '../replication/ScheduleCard'
import { SegmentingStrategiesCard } from '../replication/SegmentingStrategiesCard'
import { TrackingCard } from '../replication/TrackingCard'
import type { ConnectionView, ReplicationTaskConfig } from '../types'

/** The Overview's sub-tabs: Notes at the replication's own address, the others under `overview/`. */
export type OverviewTab = 'notes' | 'pipeline' | 'provisioning' | 'transforms' | 'segmenting'

const TABS: { tab: OverviewTab; path: string | null; label: string; testId: string }[] = [
  { tab: 'notes', path: null, label: 'Notes', testId: 'overview-tab-notes' },
  { tab: 'pipeline', path: 'overview/pipeline', label: 'Pipeline', testId: 'overview-tab-pipeline' },
  { tab: 'provisioning', path: 'overview/provisioning', label: 'Provisioning', testId: 'overview-tab-provisioning' },
  { tab: 'transforms', path: 'overview/transforms', label: 'Custom Transforms', testId: 'overview-tab-transforms' },
  { tab: 'segmenting', path: 'overview/segmenting', label: 'Bulk Load', testId: 'overview-tab-segmenting' },
]

/** Which sub-tab an address after `replications/<name>/` opens: Notes for the replication's own address and for one it does not know. */
export function overviewTabOf(rest: string): OverviewTab {
  const m = /^overview(?:\/([^/]+))?\/?$/.exec(rest)
  return TABS.find((t) => t.path === `overview/${m?.[1] ?? ''}`)?.tab ?? 'notes'
}

/**
 * A replication's Overview: the source and target on top, then the schedule and where the tracking state is kept, then the sub-tabs. The endpoints
 * stay above everything because every setting below is about what happens between those two. Each sub-tab has its own address, so a reload or a
 * link opens the same one, and every sub-tab edits the same draft of `task.yaml`, so moving between them keeps an unsaved edit.
 */
export function OverviewPanel({ project, id, replication, rest, draft, setDraft, connections, mappingCount, mappingNames = [], editable = true }: {
  project: string
  id: string
  replication: string
  /** The address after `replications/<name>/`. */
  rest: string
  draft: ReplicationTaskConfig
  setDraft: (next: ReplicationTaskConfig) => void
  connections: ConnectionView[]
  mappingCount: number
  /** The replication's mappings, which a strategy's **Try** runs over. */
  mappingNames?: string[]
  editable?: boolean
}) {
  const tab = overviewTabOf(rest)
  const base = designerPath(project, id, `replications/${replication}`)
  const tabs: SubTab[] = TABS.map(({ path, label, testId }) => ({ path, label, testId }))
  const source = connections.find((c) => c.name === draft.endpoints.source?.connectionName)
  // a mapping named in the replication's plan opens that mapping's own Provisioning tab, its part of the same plan
  const mappingLink = (mapping: string) => (
    <Link to={designerPath(project, id, `replications/${replication}/mappings/${mapping}/provisioning`)} className="mono" data-testid={`provisioning-mapping-link-${mapping}`}>{mapping}</Link>
  )

  return (
    <div className="pane" data-testid="replication-overview" data-subtab={tab}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
        <EndpointsCard
          endpoints={draft.endpoints}
          connections={connections}
          mappingCount={mappingCount}
          onChange={(endpoints) => setDraft({ ...draft, endpoints })}
        />
        <ScheduleCard draft={draft} onChange={setDraft} />
        <TrackingCard draft={draft} onChange={setDraft} editable={editable} />

        <SubTabs base={base} tabs={tabs} testId="overview-subtabs" />

        <div className="subtab-panel" data-testid={`overview-panel-${tab}`}>
          {tab === 'notes' && (
            <NotesPanel value={draft.notes} onChange={(notes) => setDraft({ ...draft, notes })} subject="this replication" testId="replication-notes" editable={editable} />
          )}
          {tab === 'pipeline' && (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
              <PipelineCard draft={draft} setDraft={setDraft} connections={connections} mappingCount={mappingCount} editable={editable} />
              <ReconcileConfigCard reconcile={reconcileOf(draft.reconcile)} writerKind={draft.changeProcessing.writer.kind}
                onChange={(reconcile) => setDraft({ ...draft, reconcile })} editable={editable} />
            </div>
          )}
          {tab === 'provisioning' && (
            <ProvisioningTab project={project} id={id} replication={replication} draft={draft} setDraft={setDraft} mappingLink={mappingLink} editable={editable} />
          )}
          {tab === 'transforms' && (
            // these slots are source-side, so a replication that binds nothing takes what its source connection binds
            <ScriptBindingsCard bindings={draft.scripts ?? {}} inherited={source?.scripts ?? {}} level="replication"
              onChange={(scripts) => setDraft({ ...draft, scripts })} editable={editable} />
          )}
          {tab === 'segmenting' && (
            <SegmentingStrategiesCard strategies={draft.segmentingStrategies ?? []} trial={{ project, id, replication, mappings: mappingNames }}
              onChange={(segmentingStrategies) => setDraft({ ...draft, segmentingStrategies })} editable={editable} />
          )}
        </div>
      </div>
    </div>
  )
}
