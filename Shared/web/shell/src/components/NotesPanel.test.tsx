import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { NotesPanel } from './NotesPanel'

const render = (value: string | null, editable = true) =>
  renderToStaticMarkup(<NotesPanel value={value} onChange={() => {}} subject="this replication" testId="n" editable={editable} />)

describe('a notes field', () => {
  it('shows the notes rendered, with Edit', () => {
    const html = render('Owned by **the data team**.')
    expect(html).toContain('data-testid="n-rendered"')
    expect(html).toContain('<strong>the data team</strong>')
    expect(html).toContain('>Edit<')
  })

  it('says what notes are for when none are written, and offers to write them', () => {
    const html = render(null)
    expect(html).toContain('data-testid="n-empty"')
    expect(html).toContain('this replication')
    expect(html).toContain('Write notes')
  })

  it('offers no edit to a person who may not change the file', () => {
    expect(render('x', false)).not.toContain('n-edit-toggle')
  })
})
