import { EndpointSidePair } from '../components/EndpointSidePair'
import type { SyncDraft } from '../useSyncDraft'
import { MappingSide } from './MappingSide'

/**
 * A mapping's source and target as the same cards the editor draws, read only (a disabled fieldset), for a page beside the editor that is about the
 * mapping as it is rather than a place to change it.
 */
export function SavedMappingHeading({ model, name }: { model: SyncDraft; name: string }) {
  const mapping = model.mappingOf(name)?.value
  const task = model.task.value
  const source = mapping?.sources[0]
  const target = mapping?.targets[0]
  if (!source || !target) return null
  return (
    <fieldset disabled style={{ border: 'none', padding: 0, margin: 0, display: 'flex', flexDirection: 'column', gap: 14 }} data-testid="saved-mapping-heading">
      <EndpointSidePair
        source={<MappingSide side="source" label="Source" inherited={task?.endpoints.source ?? null} spec={source} connections={model.connections} onChange={noop} testIdPrefix="source" />}
        target={<MappingSide side="target" label="Target" inherited={task?.endpoints.target ?? null} spec={target} connections={model.connections} onChange={noop} testIdPrefix="target" allowNewTable />}
      />
    </fieldset>
  )
}

/** Nothing here writes: the fieldset stops the controls from firing it. */
function noop() {}
