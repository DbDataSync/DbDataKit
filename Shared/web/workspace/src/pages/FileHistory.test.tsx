import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import type { Commit } from '../api'
import { FileHistoryView } from './FileHistory'

const commits: Commit[] = [
  { sha: 'a'.repeat(40), message: 'Title the grid', author: 'ada', when: '2026-11-11T10:00:00Z' },
  { sha: 'b'.repeat(40), message: 'First', author: 'root', when: '2026-11-10T10:00:00Z' },
]
const draw = (over: Partial<Parameters<typeof FileHistoryView>[0]> = {}) => renderToStaticMarkup(
  <FileHistoryView commits={commits} loading={false} chosen={null} earlier={null} loadingText={false} current="now" path="models/a.yaml" canRestore onChoose={() => {}} onRestore={() => {}} {...over} />,
)

describe('the earlier texts of a file', () => {
  it('lists the commits that changed it with who and a button to open each', () => {
    const html = draw()
    expect((html.match(/data-testid="wsp-file-history-commit"/g) ?? []).length).toBe(2)
    expect(html).toContain('Title the grid')
    expect(html).toContain('aaaaaaaa')
    expect(html).not.toContain('wsp-file-history-diff"')
  })
  it('says so when no commit has changed the file', () => {
    expect(draw({ commits: [] })).toContain('data-testid="wsp-file-history-none"')
  })
  it('shows the chosen text beside the current one and offers to put it into the editor', () => {
    const html = draw({ chosen: commits[1].sha, earlier: 'before' })
    expect(html).toContain('data-testid="wsp-file-history-diff"')
    expect(html).toMatch(/data-testid="wsp-file-history-restore"/)
    expect(html).not.toMatch(/disabled=""[^>]*data-testid="wsp-file-history-restore"|data-testid="wsp-file-history-restore"[^>]*disabled/)
  })
  it('does not offer to restore a text the editor already has, nor to a person who cannot edit', () => {
    expect(draw({ chosen: commits[0].sha, earlier: 'now' })).toContain('The editor already has this text.')
    expect(draw({ chosen: commits[0].sha, earlier: 'now' })).toMatch(/disabled=""[^>]*data-testid="wsp-file-history-restore"|data-testid="wsp-file-history-restore"[^>]*disabled/)
    expect(draw({ chosen: commits[1].sha, earlier: 'before', canRestore: false })).toMatch(/disabled=""[^>]*data-testid="wsp-file-history-restore"|data-testid="wsp-file-history-restore"[^>]*disabled/)
  })
})
