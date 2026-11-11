import { renderToStaticMarkup } from 'react-dom/server'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import { ModuleProvider } from '../module'
import { bundledDocs, type DocsSource } from './DocsSource'
import { DocsViewerView, type DocsViewerViewProps } from './DocsViewer'

// Two modules that are not Sync: the viewer and the sources know nothing of any product, which is the point of them.
const PAGES = {
  widgets: [{ slug: 'overview', title: 'Overview' }, { slug: 'tuning', title: 'Tuning widgets' }],
  gadgets: [{ slug: 'start', title: 'Start here' }],
}
const TEXT: Record<string, string> = {
  '/docs/widgets/manifest.json': JSON.stringify({ id: 'widgets', title: 'Widget Works', pages: PAGES.widgets }),
  '/docs/gadgets/manifest.json': JSON.stringify({ id: 'gadgets', title: 'Gadget Garage', pages: PAGES.gadgets }),
  '/docs/widgets/overview.md': '# Widgets\n\nSee [tuning](tuning.md#knobs) and the [front page](../README.md).\n\n![a dial](../screenshots/dials/dial.png)\n',
  '/docs/gadgets/start.md': '# Gadgets\n\n![a lever](../screenshots/levers/lever.png)\n',
}

/** A fetch that answers from TEXT, as the console's static files would: Markdown as `text/markdown`, an unknown address as the app (HTML) would. */
const fetcher: typeof fetch = async (input) => {
  const url = String(input)
  const body = TEXT[url]
  if (body === undefined) return new Response('<html>the app</html>', { status: 200, headers: { 'content-type': 'text/html' } })
  return new Response(body, { status: 200, headers: { 'content-type': url.endsWith('.md') ? 'text/markdown; charset=utf-8' : 'application/json' } })
}

const widgets = bundledDocs({ id: 'widgets', title: 'Widget Works', fetcher })
const gadgets = bundledDocs({ id: 'gadgets', title: 'Gadget Garage', fetcher })

describe('a bundled docs source', () => {
  it('reads the module\'s pages in the order its manifest has them', async () => {
    expect((await widgets.pages()).map((p) => p.slug)).toEqual(['overview', 'tuning'])
    expect((await gadgets.pages()).map((p) => p.slug)).toEqual(['start'])
  })

  it('reads a page from under the module\'s own id, so two modules never share a page', async () => {
    expect(await widgets.page('overview')).toContain('# Widgets')
    expect(await gadgets.page('start')).toContain('# Gadgets')
  })

  it('refuses an answer that is not Markdown rather than showing it as a document', async () => {
    await expect(gadgets.page('overview')).rejects.toThrow('did not answer with a document')
  })

  it('serves a module\'s pictures from under its own id', () => {
    expect(widgets.asset('../screenshots/dials/dial.png')).toBe('/docs/widgets/assets/screenshots/dials/dial.png')
    expect(gadgets.asset('../screenshots/levers/lever.png')).toBe('/docs/gadgets/assets/screenshots/levers/lever.png')
  })

  it('takes nothing but the module\'s own pictures for one', () => {
    for (const src of ['https://example.com/a.png', '//example.com/a.png', '../secrets/a.png', '../screenshots/a/b/c.png', '../screenshots/x/a.svg', '/etc/passwd'])
      expect(widgets.asset(src), src).toBeNull()
  })

  it('can be put where the host serves it', () => {
    expect(bundledDocs({ id: 'widgets', title: 'W', base: '/elsewhere/w/' }).asset('../screenshots/dials/dial.png')).toBe('/elsewhere/w/assets/screenshots/dials/dial.png')
  })

  it('is one implementation of an interface: a source from anywhere else shows the same way', async () => {
    const elsewhere: DocsSource = {
      id: 'remote', title: 'Remote product',
      pages: async () => [{ slug: 'only', title: 'Only page' }],
      page: async () => '# From a paired server',
      asset: () => null,
    }
    expect((await elsewhere.pages())[0]!.title).toBe('Only page')
  })
})

function view(over: Partial<DocsViewerViewProps> & Pick<DocsViewerViewProps, 'title' | 'base' | 'pages'>, source: DocsSource, route = '/') {
  return renderToStaticMarkup(
    <MemoryRouter initialEntries={[route]}>
      <DocsViewerView page={undefined} text={undefined} loading={false} error={null} versionNote="for the build" asset={(src) => source.asset(src)} {...over} />
    </MemoryRouter>)
}

describe('the viewer', () => {
  it('shows each module\'s index under that module\'s own route and says whose docs they are', () => {
    const w = view({ title: 'Widget Works', base: '/docs/widgets', pages: PAGES.widgets }, widgets)
    const g = view({ title: 'Gadget Garage', base: '/docs/gadgets', pages: PAGES.gadgets }, gadgets)

    expect(w).toContain('data-testid="docs-index"')
    expect(w).toContain('version of Widget Works you are running')
    expect(w).toContain('href="/docs/widgets/tuning"')
    expect(w).toContain('Tuning widgets')
    expect(w).not.toContain('Start here')
    expect(g).toContain('version of Gadget Garage you are running')
    expect(g).toContain('href="/docs/gadgets/start"')
    expect(g).not.toContain('Tuning widgets')
  })

  it('shows the note about which version the docs describe', () => {
    expect(view({ title: 'W', base: '/docs', pages: PAGES.widgets, versionNote: 'For the version running here: 7.1' }, widgets)).toContain('For the version running here: 7.1')
  })

  it('renders a page with its links made under the base and its pictures from the module', async () => {
    const html = view({ title: 'Widget Works', base: '/docs/widgets', pages: PAGES.widgets, page: 'overview', text: await widgets.page('overview') }, widgets)

    expect(html).toContain('data-testid="docs-content"')
    expect(html).toContain('href="/docs/widgets/tuning#knobs"')
    // The README link every page opens with is the docs index of the module.
    expect(html).toContain('href="/docs/widgets"')
    expect(html).toContain('src="/docs/widgets/assets/screenshots/dials/dial.png"')
  })

  it('shows another module\'s page the same way, with its own picture', async () => {
    const html = view({ title: 'Gadget Garage', base: '/docs/gadgets', pages: PAGES.gadgets, page: 'start', text: await gadgets.page('start') }, gadgets)

    expect(html).toContain('src="/docs/gadgets/assets/screenshots/levers/lever.png"')
    expect(html).not.toContain('/docs/widgets')
  })

  it('makes a link under the module\'s own base when it is mounted in a host, which prefixes every route', () => {
    const html = renderToStaticMarkup(
      <MemoryRouter>
        <ModuleProvider info={{ basePath: '/servers/w1', apiBase: '/relay/w1' }}>
          <DocsViewerView title="W" base="/docs" pages={PAGES.widgets} page={undefined} text={undefined} loading={false} error={null} versionNote="" asset={() => null} />
        </ModuleProvider>
      </MemoryRouter>)

    expect(html).toContain('href="/servers/w1/docs/overview"')
  })

  it('says a page is not part of the docs, and offers the index, for an address nobody has', () => {
    const html = view({ title: 'Widget Works', base: '/docs/widgets', pages: PAGES.widgets, page: 'no-such-page' }, widgets)

    expect(html).toContain('data-testid="docs-not-found"')
    expect(html).toContain('no-such-page')
    expect(html).toContain('href="/docs/widgets"')
  })

  it('says loading, not not-found, while the page list is on its way', () => {
    const html = view({ title: 'W', base: '/docs/widgets', pages: [], page: 'overview', loading: true }, widgets)

    expect(html).not.toContain('docs-not-found')
  })
})
