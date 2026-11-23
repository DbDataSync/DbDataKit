import { useState } from 'react'
import { Markdown } from './Markdown'

/**
 * A Markdown notes field, read by default and edited on request. Most visits read the notes, so they open rendered; **Edit** opens the source.
 * With nothing written the card says what notes are for. An empty text is handed up as null: a notes key that exists and says nothing is not kept.
 * `editable` false leaves the notes read only (a person who may not change the file).
 */
export function NotesPanel({ value, onChange, subject, testId = 'notes', editable = true }: {
  value: string | null | undefined
  onChange: (next: string | null) => void
  /** What these notes are about, for the empty state's prompt: "this replication", "this mapping". */
  subject: string
  testId?: string
  editable?: boolean
}) {
  const [editing, setEditing] = useState(false)
  const text = value ?? ''

  return (
    <div className="card" data-testid={`${testId}-card`}>
      <div className="card-head">
        <span className="card-title">Notes</span>
        <span className="card-note">Markdown, kept in the file with every other setting</span>
        {editable && (
          <button
            type="button"
            className="btn btn-sm spacer"
            onClick={() => setEditing(!editing)}
            aria-pressed={editing}
            data-testid={`${testId}-edit-toggle`}
          >
            {editing ? 'Done' : text ? 'Edit' : 'Write notes'}
          </button>
        )}
      </div>

      <div className="card-body">
        {editing && editable ? (
          <textarea
            className="input notes-editor"
            value={text}
            autoFocus
            placeholder={`What the next person needs to know about ${subject}.`}
            onChange={(e) => onChange(e.target.value.trim() ? e.target.value : null)}
            data-testid={`${testId}-editor`}
          />
        ) : text ? (
          <Markdown text={text} testId={`${testId}-rendered`} />
        ) : (
          <span className="hint" data-testid={`${testId}-empty`}>
            Nothing written yet. Notes are for what the next person needs to know about {subject}: who owns it, why a setting is the way it is,
            what broke last time.
          </span>
        )}
      </div>
    </div>
  )
}
