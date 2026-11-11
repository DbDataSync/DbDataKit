import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { MemoryRouter } from 'react-router-dom'
import { emptyPublishForm, type Prepared, type Published } from '../publishFiles'
import { PublishFromFilesView, type PublishFromFilesViewProps } from './PublishFromFiles'

const prepared: Prepared = { manifest: 'schema: package/1\nname: orders\n', name: 'orders', files: [] }
function props(over: Partial<PublishFromFilesViewProps> = {}): PublishFromFilesViewProps {
  return {
    canPublish: true, folder: { count: 3, bytes: 2048, hashing: false, paths: ['app.dll', 'notes.txt', 'package.yaml'] }, manifest: { ok: true, name: 'orders' }, manifestFrom: 'folder', prepared,
    form: { ...emptyPublishForm, channel: 'dev' }, busy: false, step: null, failure: null, result: null, onFolder: () => {}, onManifest: () => {}, onForm: () => {}, onPublish: () => {}, ...over,
  }
}
const html = (over?: Partial<PublishFromFilesViewProps>) => renderToStaticMarkup(<MemoryRouter>{PublishFromFilesView(props(over))}</MemoryRouter>)

function find(node: ReactNode, testId: string): ReactElement<Record<string, unknown>> | undefined {
  if (Array.isArray(node)) { for (const child of node) { const found = find(child, testId); if (found) return found } return undefined }
  if (!isValidElement(node)) return undefined
  const element = node as ReactElement<Record<string, unknown>>
  if (element.props['data-testid'] === testId) return element
  if (typeof element.type === 'function') return find((element.type as (p: unknown) => ReactNode)(element.props), testId)
  return find(element.props.children as ReactNode, testId)
}
const prop = (over: Partial<PublishFromFilesViewProps>, id: string, name: string) => find(PublishFromFilesView(props(over)), id)!.props[name]

const published: Published = { kind: 'integration', name: 'orders', channel: 'dev', version: 2, changed: true, mode: 'stage', signature: 'unsigned', tasks: ['load', 'check'], uploaded: 2, skipped: 1 }

describe('publishing a package from files', () => {
  it('lists the files and the package the manifest names', () => {
    const text = html()
    expect(text).toContain('3 file(s), 2.0 KB')
    expect(text).toContain('data-package="orders"')
    expect(text).toContain('package.yaml in the folder')
    expect(text.match(/runner-pub-file"/g)).toHaveLength(3)
  })
  it('shows only the first paths of a long list and says how many more', () => {
    const paths = Array.from({ length: 20 }, (_, i) => `f${i}.dll`)
    const text = html({ folder: { count: 20, bytes: 1, hashing: false, paths } })
    expect(text.match(/runner-pub-file"/g)).toHaveLength(12)
    expect(text).toContain('and 8 more')
  })
  it('says nothing is chosen yet, and disables Publish with the reason', () => {
    expect(prop({ folder: null, manifest: null, manifestFrom: null, prepared: null }, 'runner-pub-button', 'disabled')).toBe(true)
    expect(html({ folder: null, manifest: null, manifestFrom: null, prepared: null })).toContain('Choose the folder of the package.')
  })
  it('names a manifest that is not one, and an empty folder', () => {
    expect(html({ manifest: { ok: false, problem: 'The manifest is "build/1", not package/1.' }, prepared: null })).toContain('not package/1')
    expect(html({ folder: { count: 0, bytes: 0, hashing: false, paths: [] }, prepared: null })).toContain('The folder has no file.')
  })
  it('waits for the files to be read, then enables Publish once a channel is named', () => {
    expect(html({ folder: { count: 3, bytes: 1, hashing: true, paths: [] }, prepared: null })).toContain('Reading the files...')
    expect(prop({ form: { ...emptyPublishForm, channel: '' } }, 'runner-pub-button', 'disabled')).toBe(true)
    expect(html({ form: { ...emptyPublishForm, channel: '' } })).toContain('Name the channel.')
    expect(prop({}, 'runner-pub-button', 'disabled')).toBe(false)
  })
  it('does not offer anyone but an administrator the folder or the button, and says why', () => {
    expect(prop({ canPublish: false }, 'runner-pub-button', 'disabled')).toBe(true)
    expect(prop({ canPublish: false }, 'runner-pub-folder', 'disabled')).toBe(true)
    expect(html({ canPublish: false })).toContain('needs the admin role')
  })
  it('shows the step while it is busy and the runner\'s refusal after', () => {
    const busy = html({ busy: true, step: { kind: 'sending', done: 0, total: 2, path: 'app.dll' } })
    expect(busy).toContain('Sending app.dll (1 of 2)...')
    expect(prop({ busy: true }, 'runner-pub-button', 'disabled')).toBe(true)
    expect(html({ failure: 'The runner refused the file app.dll (400): too big' })).toContain('The runner refused the file app.dll')
  })
  it('shows the staged version and its tasks after, with a link to the package', () => {
    const text = html({ result: published })
    expect(text).toContain('Version 2 of orders is staged on dev')
    expect(text).toContain('2 file(s) sent, 1 already on the runner.')
    expect(text).toContain('data-task="check"')
    expect(text).toContain('/packages/orders')
  })
  it('sends the typed channel and the serve choice, and presses Publish', () => {
    const forms: unknown[] = []
    let pressed = 0
    const view = PublishFromFilesView(props({ onForm: (f) => forms.push(f), onPublish: () => { pressed++ } }))
    ;(find(view, 'runner-pub-channel')!.props.onChange as (e: { target: { value: string } }) => void)({ target: { value: 'prod' } })
    ;(find(view, 'runner-pub-serve')!.props.onChange as (e: { target: { checked: boolean } }) => void)({ target: { checked: true } })
    ;(find(view, 'runner-pub-button')!.props.onClick as () => void)()
    expect(forms).toEqual([{ channel: 'prod', serve: false, note: '' }, { channel: 'dev', serve: true, note: '' }]); expect(pressed).toBe(1)
  })
})
