import { useState } from 'react'
import { CodeEditor, Field } from '@dbdatakit/shell'

/**
 * The mapping's source filter, a row of its own beneath both sides so the two sides stay the same height and comparable. Closed when empty (most
 * mappings read the whole table); open by itself when there is a filter, with **FILTERS APPLIED** on its heading, since a filter narrowing what a
 * replication reads is the first thing to see when rows are missing. The predicate is put into the reader's WHERE clause as written.
 */
export function SourceFilterCard({ filter, onChange, editable = true }: {
  filter: string | null
  onChange: (next: string | null) => void
  editable?: boolean
}) {
  const [expanded, setExpanded] = useState(false)
  const applied = !!filter?.trim()
  const open = expanded || applied

  return (
    <div className="card" data-testid="source-filter-card">
      <div className="card-head">
        <button type="button" className="btn-link" onClick={() => setExpanded(!open)} aria-expanded={open} data-testid="source-filter-toggle">
          {open ? '▾' : '▸'} Source filter
        </button>
        {applied
          ? <span className="badge badge-accent" data-testid="source-filter-applied-pill">FILTERS APPLIED</span>
          : <span className="card-note">optional SQL predicate; the whole table is read without one</span>}
      </div>
      {open && (
        <div className="card-body">
          <Field label="Source filter: optional SQL predicate">
            <CodeEditor value={filter ?? ''} language="sql" readOnly={!editable} onChange={(next) => onChange(next.trim() ? next : null)} minLines={2} maxLines={8} testId="source-filter-editor" />
          </Field>
        </div>
      )}
    </div>
  )
}
