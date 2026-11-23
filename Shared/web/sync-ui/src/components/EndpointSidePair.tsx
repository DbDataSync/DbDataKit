import type { ReactNode } from 'react'

export type EndpointSide = 'source' | 'target'

/**
 * Source and target side by side, with the arrow between them: one layout for the replication's endpoints and a mapping's two sides, which answer
 * the same question ("where does this read from and write to"). The pair owns the card shells, the arrow, the equal heights and each side's accent;
 * a caller supplies what goes inside each card.
 */
export function EndpointSidePair({ source, target }: { source: ReactNode; target: ReactNode }) {
  return (
    <div className="side-pair">
      {source}
      <span className="side-arrow" aria-hidden="true"><span>→</span></span>
      {target}
    </div>
  )
}

/** One card of the pair, with the caller's own content in its head (a note, a badge, a toggle). */
export function EndpointSideCard({ side, title, head, children, testId }: {
  side: EndpointSide
  title: string
  /** Drawn in the card head after the title. */
  head?: ReactNode
  children: ReactNode
  testId?: string
}) {
  return (
    <div className={`card side-${side}`} data-testid={testId} data-side={side}>
      <div className="card-head tight">
        <span className={`card-title sm side-${side}`}>{title}</span>
        {head}
      </div>
      <div className="card-body">{children}</div>
    </div>
  )
}
