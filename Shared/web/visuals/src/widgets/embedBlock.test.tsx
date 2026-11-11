// @vitest-environment happy-dom
import { act } from 'react'
import { createRoot } from 'react-dom/client'
import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { EmbedDoc } from '../views/model'
import { EMBED_FRAME_TIMEOUT_MS, EmbedBlock, embedOf, isSafeEmbedUrl } from './EmbedBlock'

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

  describe('a frame that does not load (plan 26 AP17)', () => {
    afterEach(() => { vi.useRealTimers(); document.body.innerHTML = '' })
    const mount = (framed: boolean) => {
      ;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true
      const host = document.createElement('div')
      document.body.appendChild(host)
      const root = createRoot(host)
      act(() => root.render(<EmbedBlock widget={widget} result={{ type: 'embed', url: widget.url, framed }} />))
      return host
    }

    it('always carries the link beside a frame, because a sign-in failure page cannot be told from a report', () => {
      const html = renderToStaticMarkup(<EmbedBlock widget={widget} result={{ type: 'embed', url: widget.url, framed: true }} />)
      expect(html).toContain('data-testid="vz-embed-open"')
      expect(html).toContain('>Open it<')
    })

    it('is replaced by the link and a visible reason when the frame never loads', () => {
      vi.useFakeTimers()
      const host = mount(true)
      expect(host.querySelector('iframe')).not.toBeNull()
      act(() => { vi.advanceTimersByTime(EMBED_FRAME_TIMEOUT_MS + 1) })
      expect(host.querySelector('iframe')).toBeNull()
      expect(host.querySelector('[data-testid="vz-embed-failed"]')?.textContent).toContain('Cannot show this report')
      expect(host.querySelector('[data-testid="vz-embed"]')?.getAttribute('data-mode')).toBe('link')
      expect(host.querySelector('a')?.getAttribute('href')).toBe(widget.url)
    })

    it('stays a frame once it has loaded', () => {
      vi.useFakeTimers()
      const host = mount(true)
      act(() => { host.querySelector('iframe')!.dispatchEvent(new Event('load')) })
      act(() => { vi.advanceTimersByTime(EMBED_FRAME_TIMEOUT_MS * 2) })
      expect(host.querySelector('iframe')).not.toBeNull()
      expect(host.querySelector('[data-testid="vz-embed"]')?.getAttribute('data-state')).toBe('loaded')
    })

    it('shows no failure for a plain link', () => {
      const host = mount(false)
      expect(host.querySelector('[data-testid="vz-embed-failed"]')).toBeNull()
      expect(host.querySelector('[data-testid="vz-embed"]')?.getAttribute('data-state')).toBe('link')
    })
  })
})
