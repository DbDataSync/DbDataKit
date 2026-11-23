import { Field } from '@dbdatakit/shell'
import { runsAgainstAConnection, type BatchReloadSegment, type SegmentingStrategyConfig } from '../types'

export type SegmentingMode = 'none' | 'full' | 'auto' | 'custom' | 'static'

/** What the list of segments is: nothing (the whole table), one entry that stands for the whole table (full, auto, custom), or a fixed list. */
export function segmentingModeOf(segments: readonly BatchReloadSegment[]): SegmentingMode {
  if (segments.length === 0) return 'none'
  const single = segments.length === 1 ? segments[0] : undefined
  return single?.mode === 'full' ? 'full' : single?.mode === 'auto' ? 'auto' : single?.mode === 'custom' ? 'custom' : 'static'
}

/** The segments after choosing a mode: full, auto and custom replace the list with their one entry; a fixed list starts with one empty range. */
export function segmentsForMode(mode: SegmentingMode, strategies: readonly SegmentingStrategyConfig[]): BatchReloadSegment[] {
  switch (mode) {
    case 'none': return []
    case 'full': return [{ mode: 'full' }]
    case 'auto': return [{ mode: 'auto', column: '', bucketCount: 4 }]
    case 'custom': return [{ mode: 'custom', strategyName: strategies[0]?.name ?? '', column: '' }]
    case 'static': return [{ mode: 'range', column: '', rangeMin: '', rangeMax: '' }]
  }
}

/**
 * How this mapping divides for a reload by default (`defaultSegmenting:` of the mapping): what a scheduled `BatchReload` pass processes and what a
 * bulk load starts from. Any number of list and range entries side by side, or one entry (full, auto, custom) that expands into many when the
 * reload runs.
 */
export function DefaultSegmentingCard({ segments, strategies, onChange, editable = true }: {
  segments: BatchReloadSegment[]
  strategies: SegmentingStrategyConfig[]
  onChange: (segments: BatchReloadSegment[]) => void
  editable?: boolean
}) {
  const replace = (index: number, segment: BatchReloadSegment) => onChange(segments.map((s, i) => (i === index ? segment : s)))
  const remove = (index: number) => onChange(segments.filter((_, i) => i !== index))
  const setSingle = (segment: BatchReloadSegment) => onChange([segment])
  const single = segments.length === 1 ? segments[0] : undefined
  const mode = segmentingModeOf(segments)
  const strategy = single?.mode === 'custom' ? strategies.find((s) => s.name === single.strategyName) : undefined

  return (
    <div className="card" data-testid="default-segmenting-card">
      <div className="card-head"><span className="card-title">Default reload segmenting</span></div>
      <fieldset className="card-body" style={{ gap: 10, border: 0, margin: 0 }} disabled={!editable}>
        <Field label="How this table divides for a reload">
          <select className="select" value={mode} onChange={(e) => onChange(segmentsForMode(e.target.value as SegmentingMode, strategies))} data-testid="default-segmenting-mode">
            <option value="none">Full: the whole table, unsegmented</option>
            <option value="full">Full: stated explicitly</option>
            <option value="auto">Auto: split a column into buckets</option>
            <option value="custom">Custom: a segmenting strategy</option>
            <option value="static">A fixed list of segments</option>
          </select>
        </Field>

        {mode === 'none' && <span className="hint" data-testid="default-segmenting-none-hint">Nothing set, so a reload covers the whole table.</span>}

        {single?.mode === 'auto' && (
          <div className="row" style={{ gap: 8 }}>
            <Field label="Column"><input className="input" value={single.column} onChange={(e) => setSingle({ ...single, column: e.target.value })} data-testid="default-segmenting-auto-column" /></Field>
            <Field label="Buckets"><input className="input" type="number" min={1} value={single.bucketCount} onChange={(e) => setSingle({ ...single, bucketCount: Number(e.target.value) })} data-testid="default-segmenting-auto-buckets" /></Field>
          </div>
        )}

        {single?.mode === 'custom' && (
          <>
            <Field label="Strategy">
              <select className="select" value={single.strategyName} onChange={(e) => setSingle({ ...single, strategyName: e.target.value })} data-testid="default-segmenting-strategy">
                <option value="">Pick a strategy…</option>
                {single.strategyName && !strategy && <option value={single.strategyName}>{single.strategyName}, not a strategy of the replication</option>}
                {strategies.map((s) => <option key={s.name} value={s.name}>{s.name} ({s.kind})</option>)}
              </select>
            </Field>
            {strategy?.kind !== 'Script' && (
              <Field label="Column">
                <input className="input" value={single.column ?? ''} onChange={(e) => setSingle({ ...single, column: e.target.value })} placeholder="OrderDate" data-testid="default-segmenting-custom-column" />
              </Field>
            )}
            {strategies.length === 0 && <span className="hint" data-testid="default-segmenting-no-strategies">The replication defines no segmenting strategy yet (its Overview, Bulk Load).</span>}
            <span className="hint">Kept as a reference, not as the segments it makes, so a strategy for "the last three months" keeps meaning that.</span>
            {strategy && runsAgainstAConnection(strategy.kind) && (
              <span className="hint warn" data-testid="default-segmenting-repeat-warning">
                <strong>This query runs on every scheduled pass.</strong> <span className="mono">{strategy.name}</span> reads the {strategy.kind === 'TargetSql' ? 'target' : 'source'} database
                each time the replication runs, not once.
              </span>
            )}
          </>
        )}

        {mode === 'static' && (
          <>
            {segments.map((segment, index) => (
              <div key={index} className="row" style={{ gap: 8, alignItems: 'flex-end' }} data-testid={`default-segmenting-row-${index}`}>
                <Field label="Kind">
                  <select className="select" value={segment.mode}
                    onChange={(e) => replace(index, e.target.value === 'list' ? { mode: 'list', column: '', values: [] } : { mode: 'range', column: '', rangeMin: '', rangeMax: '' })}
                    data-testid={`default-segmenting-kind-${index}`}>
                    <option value="range">Range</option>
                    <option value="list">List</option>
                  </select>
                </Field>
                {(segment.mode === 'range' || segment.mode === 'list') && (
                  <Field label="Column"><input className="input" value={segment.column} onChange={(e) => replace(index, { ...segment, column: e.target.value })} data-testid={`default-segmenting-column-${index}`} /></Field>
                )}
                {segment.mode === 'range' && (
                  <>
                    <Field label="From (inclusive)"><input className="input" value={segment.rangeMin} onChange={(e) => replace(index, { ...segment, rangeMin: e.target.value })} data-testid={`default-segmenting-min-${index}`} /></Field>
                    <Field label="To (exclusive)"><input className="input" value={segment.rangeMax} onChange={(e) => replace(index, { ...segment, rangeMax: e.target.value })} data-testid={`default-segmenting-max-${index}`} /></Field>
                  </>
                )}
                {segment.mode === 'list' && (
                  <Field label="Values (comma-separated)">
                    <input className="input" value={segment.values.join(', ')} onChange={(e) => replace(index, { ...segment, values: e.target.value.split(',').map((v) => v.trim()).filter(Boolean) })}
                      data-testid={`default-segmenting-values-${index}`} />
                  </Field>
                )}
                <button type="button" className="btn-link quiet" onClick={() => remove(index)} data-testid={`default-segmenting-remove-${index}`}>Remove</button>
              </div>
            ))}
            <div className="row" style={{ gap: 8 }}>
              <button type="button" className="btn" onClick={() => onChange([...segments, { mode: 'range', column: '', rangeMin: '', rangeMax: '' }])} data-testid="default-segmenting-add-range">Add range</button>
              <button type="button" className="btn" onClick={() => onChange([...segments, { mode: 'list', column: '', values: [] }])} data-testid="default-segmenting-add-list">Add list</button>
            </div>
          </>
        )}
      </fieldset>
    </div>
  )
}
