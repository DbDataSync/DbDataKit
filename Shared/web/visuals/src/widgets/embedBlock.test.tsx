import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import type { EmbedDoc } from '../views/model'
import { EmbedBlock, embedOf, isSafeEmbedUrl } from './EmbedBlock'

const widget: EmbedDoc = { type: 'embed', title: 'Sales report', url: 'https://reports.example.com/r?x=1', link_text: 'Open it' }

describe('the embed widget (DBR19)', () => {
  it('is a link from the document alone, and a link when the server did not approve the origin', () => {
    const html = renderToStaticMarkup(<EmbedBlock widget={widget} />)
    expect(html).toContain('data-mode="link"')
    expect(html).toContain('rel="noopener noreferrer"')
    expect(html).toContain('>Open it<')
    expect(html).not.toContain('<iframe')
    expect(renderToStaticMarkup(<EmbedBlock widget={widget} result={{ ...embedOf(widget), framed: false }} />)).toContain('data-mode="link"')
  })

  it('frames only what the server says is framed, sandboxed and with no referrer', () => {
    const html = renderToStaticMarkup(<EmbedBlock widget={widget} result={{ type: 'embed', url: widget.url, framed: true }} />)
    expect(html).toContain('<iframe')
    expect(html).toContain('sandbox="allow-scripts allow-same-origin allow-forms allow-popups"')
    expect(html).toContain('referrerPolicy="no-referrer"')
    expect(html).toContain('data-mode="frame"')
  })

  it('draws nothing of an address that is not plain https', () => {
    for (const url of ['http://reports.example.com/r', 'javascript:alert(1)', 'https://user:pw@reports.example.com/r', 'https://a.example.com/ r', '//reports.example.com/r', 'data:text/html,x']) {
      expect(isSafeEmbedUrl(url), url).toBe(false)
      const html = renderToStaticMarkup(<EmbedBlock widget={widget} result={{ type: 'embed', url, framed: true }} />)
      expect(html).not.toContain('<iframe')
      expect(html).not.toContain('<a ')
    }
    expect(isSafeEmbedUrl('https://reports.example.com/r')).toBe(true)
  })
})
