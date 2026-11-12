import { useEffect, useRef, type KeyboardEvent } from 'react'
import { readFromTarget } from './surface'

/** An open dialog or panel: the link's words, how it is shown, and the address of its target. */
export interface OpenSurface { as: 'dialog' | 'panel'; label: string; address: string }

/**
 * The shell's dialog or side panel (plan 32 DBR36): the link's target in a **sandboxed frame of its own** (`sandbox="allow-scripts allow-forms"`, an opaque origin: the target is an app's page, it gets no cookies from
 * this page and no reach into it), over the dashboard. A dialog is modal (`role="dialog"`, `aria-modal`; the host puts `inert` on the page behind it); a panel is a side sheet that leaves the page usable.
 * Focus moves into the surface when it opens and **returns to what had it** when it closes (a custom widget's own frame included); Escape closes. The frame says `views:saved` when its write was accepted (the surface closes
 * and says it saved) or `views:close`; nothing else it posts is read, and only from this frame's window.
 */
export function LinkSurface({ surface, onClose }: { surface: OpenSurface; onClose: (saved: boolean) => void }) {
  const frame = useRef<HTMLIFrameElement>(null)
  const closeButton = useRef<HTMLButtonElement>(null)
  const opener = useRef<Element | null>(typeof document === 'undefined' ? null : document.activeElement)
  const saved = useRef(false)
  const close = useRef(onClose)
  close.current = onClose

  useEffect(() => {
    closeButton.current?.focus()
    const listen = (e: MessageEvent) => {
      if (e.source !== frame.current?.contentWindow) return
      const message = readFromTarget(e.data)
      if (!message) return
      if (message.type === 'views:saved') saved.current = true
      close.current(saved.current)
    }
    window.addEventListener('message', listen)
    const returnTo = opener.current
    return () => {
      window.removeEventListener('message', listen)
      if (returnTo instanceof HTMLElement && returnTo.isConnected) returnTo.focus()
    }
  }, [])

  const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') { e.stopPropagation(); onClose(saved.current) } }
  const modal = surface.as === 'dialog'
  return (
    <div className={`vz-surface vz-surface-${surface.as}`} data-testid={`vz-surface-${surface.as}`} onKeyDown={onKey}>
      {modal ? <div className="vz-surface-scrim" data-testid="vz-surface-scrim" onClick={() => onClose(saved.current)} /> : null}
      <div className="vz-surface-box" role={modal ? 'dialog' : 'complementary'} aria-modal={modal ? true : undefined} aria-label={surface.label}>
        <header className="vz-surface-head">
          <h2>{surface.label}</h2>
          <button type="button" ref={closeButton} className="vz-button" data-testid="vz-surface-close" onClick={() => onClose(saved.current)} aria-label={`Close ${surface.label}`}>Close</button>
        </header>
        <iframe ref={frame} className="vz-surface-frame" data-testid="vz-surface-frame" title={surface.label} sandbox="allow-scripts allow-forms" referrerPolicy="no-referrer" src={surface.address} />
      </div>
    </div>
  )
}
