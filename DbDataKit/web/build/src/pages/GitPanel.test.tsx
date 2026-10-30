import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'

const shown: unknown[] = []
vi.mock('@dbdatakit/shell', async (original) => ({
  ...(await original<typeof import('@dbdatakit/shell')>()),
  DiffViewer: (props: unknown) => { shown.push(props); return <div data-testid="stand-in-diff" /> },
}))

import { ChangeDiff } from './GitPanel'

describe('ChangeDiff', () => {
  it('shows both texts side by side, in the language of the file', () => {
    shown.length = 0
    const html = renderToStaticMarkup(<ChangeDiff change={{ path: 'models/a.sql', status: 'modified', patch: '@@ x', before: 'select 1', after: 'select 2', sides: true }} />)
    expect(html).toContain('build-git-diff')
    expect(html).not.toContain('build-git-patch')
    expect(shown).toMatchObject([{ before: 'select 1', after: 'select 2', language: 'sql' }])
  })
  it('passes null for the side a new or deleted file does not have', () => {
    shown.length = 0
    renderToStaticMarkup(<ChangeDiff change={{ path: 'a.yml', status: 'added', patch: '', before: null, after: 'x: 1', sides: true }} />)
    renderToStaticMarkup(<ChangeDiff change={{ path: 'notes.txt', status: 'deleted', patch: '', before: 'x', after: null, sides: true }} />)
    expect(shown).toMatchObject([{ before: null, after: 'x: 1', language: 'yaml' }, { before: 'x', after: null, language: 'plaintext' }])
  })
  it('falls back to the patch for a binary or large file', () => {
    shown.length = 0
    const html = renderToStaticMarkup(<ChangeDiff change={{ path: 'big.sql', status: 'modified', patch: '@@ -1 +1 @@\n-a\n+b', sides: false }} />)
    expect(html).toContain('build-git-patch')
    expect(html).toContain('+b')
    expect(shown).toEqual([])
  })
})
