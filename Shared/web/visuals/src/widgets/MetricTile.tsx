import type { MetricDoc } from '../views/model'
import type { RenderedMetric } from '../views/rendered'
import type { DensityClass } from './density'

const ARROW = { up: '▲', down: '▼', flat: '▶' } as const
const WORD = { up: 'up', down: 'down', flat: 'unchanged' } as const

/**
 * A metric: one number as the server formatted it, with its label and its comparison. Density takes them away in turn (plan 32 section 3.3): from `s` the label is dropped, at `xs` the comparison too. The change
 * is told by an arrow and a word as well as a colour, so it reads without colour. Nothing is computed here; the change and its text arrive with the result.
 */
export function MetricTile({ widget, result, density = 'l' }: { widget: MetricDoc; result: RenderedMetric; density?: DensityClass }) {
  const label = widget.label ?? widget.title
  const showLabel = label !== undefined && (density === 'l' || density === 'm')
  const showCompare = result.compare && density !== 'xs'
  const value = result.value?.text ?? ''
  const change = result.compare?.change
  return (
    <div className={`vz-metric vz-d-${density}`} data-testid="vz-metric" role="group" aria-label={label ? `${label}: ${value}` : value}>
      {showLabel ? <div className="vz-metric-label">{label}</div> : null}
      <div className="vz-metric-value">{value}</div>
      {showCompare ? (
        <div className="vz-metric-compare">
          {change ? (
            <span className={`vz-change vz-${change.direction}`}>
              <span aria-hidden="true">{ARROW[change.direction]}</span>
              <span className="vz-sr">{`${WORD[change.direction]} `}</span>
              {change.text}
            </span>
          ) : null}
          <span className="vz-metric-prior">{change ? ` vs ${result.compare!.text}` : result.compare!.text}</span>
        </div>
      ) : null}
    </div>
  )
}
