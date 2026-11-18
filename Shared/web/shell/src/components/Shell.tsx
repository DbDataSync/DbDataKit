import { useEffect, useRef, useState, type ReactNode } from 'react'
import { flushSync } from 'react-dom'
import { NavLink as RouterNavLink, useLocation } from 'react-router-dom'
import { NavLink, useModule, usePathInModule } from '../module'
import { NotificationBell } from './NotificationBell'
import { SignedInAs } from './SignIn'
import { useIsAdmin } from './useIsAdmin'
import { groupTestId, isEntryActive, NAV_COLLAPSE_BELOW, NAV_COLLAPSED_KEY, startsCollapsed, toGroups, visibleGroups, type Nav, type RailItem } from './nav'

export type { Nav, NavGroup, RailItem } from './nav'

function readCollapsed(): string | null {
  try { return window.localStorage.getItem(NAV_COLLAPSED_KEY) } catch { return null }
}
function keepCollapsed(collapsed: boolean) {
  try { window.localStorage.setItem(NAV_COLLAPSED_KEY, String(collapsed)) } catch { /* a browser that keeps nothing still collapses for this page */ }
}

/**
 * Whether the navigation is the icon rail: the person's choice, kept in the browser, and always the rail while the window is narrower than 1000 pixels
 * (a window made narrow collapses it; made wide again, it goes back to the person's choice).
 */
function useCollapsed(): [boolean, () => void] {
  const [collapsed, setCollapsed] = useState(() => startsCollapsed(readCollapsed(), window.innerWidth))
  useEffect(() => {
    let narrow = window.innerWidth < NAV_COLLAPSE_BELOW
    const onResize = () => {
      const now = window.innerWidth < NAV_COLLAPSE_BELOW
      // drawn at once, inside the resize, so what measures itself on this resize (a report sheet that fits its frame) sees the new width
      if (now !== narrow) flushSync(() => setCollapsed(startsCollapsed(readCollapsed(), window.innerWidth)))
      narrow = now
    }
    window.addEventListener('resize', onResize)
    return () => window.removeEventListener('resize', onResize)
  }, [])
  // The page beside the navigation gained or lost its width without the window changing: a page that fits itself to the window (a dashboard, a
  // report's sheet) measured before the navigation changed, so it is told the room changed once the new width is drawn.
  const drawn = useRef(collapsed)
  useEffect(() => {
    if (drawn.current === collapsed) return
    drawn.current = collapsed
    window.dispatchEvent(new Event('resize'))
  }, [collapsed])
  return [collapsed, () => setCollapsed((was) => { keepCollapsed(!was); return !was })]
}

const ChevronIcon = ({ left }: { left: boolean }) => (
  <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden>
    {left ? <path d="M14 6l-6 6 6 6M19 6l-6 6 6 6" /> : <path d="M10 6l6 6-6 6M5 6l6 6-6 6" />}
  </svg>
)

/**
 * The chrome every screen sits in: the navigation on the left (named groups of labelled entries, collapsible to a 46px icon rail), a 42px breadcrumb
 * bar and a 46px tab bar. The product says what is in it: its name, its mark and its navigation (plan 13, U02); the signed-in identity and the
 * notification bell are the shell's own, on every screen, absent where the deployment does not authenticate.
 *
 * The mockups also show an environment pill, an Activity tab and a health roll-up in this bar. None of those have anything behind them, and a console that displays an
 * invented reading is worse than one that displays none.
 *
 * Design: DbDataKit/README.md#finding-a-screen-the-navigation
 */
export function Shell({ brand, mark, rail, crumbs, tabs, actions, children }: {
  /** The product's name, first in the breadcrumb bar. */
  brand: string
  /** The product's mark, at the top of the navigation. */
  mark: ReactNode
  /** The entries, as named groups or as one flat list. */
  rail: Nav
  crumbs: Crumb[]
  tabs?: ReactNode
  actions?: ReactNode
  children: ReactNode
}) {
  const isAdmin = useIsAdmin()
  const { home, host } = useModule()
  const pathname = useLocation().pathname
  const here = usePathInModule(pathname)
  const [collapsed, toggle] = useCollapsed()
  const own = visibleGroups(toGroups(rail), isAdmin)
  // Inside a host, the host's navigation is drawn and this product's entries are nested under the server's own entry; a host entry whose test id
  // this product also uses (the console's Runs beside a runner's Runs) is told apart as `rail-console-<name>`.
  const ownIds = new Set(own.flatMap((g) => g.items.map((i) => i.testId)).filter(Boolean))
  const groups = host ? visibleGroups(host.nav, isAdmin) : own

  const label = (item: RailItem) => !collapsed && (
    <>
      <span className="rail-label">{item.title}</span>
      {item.note && <span className="rail-note">{item.note}</span>}
    </>
  )
  const tip = (item: RailItem) => [item.title, item.problem ?? item.note].filter(Boolean).join(' · ')

  /** One of this product's entries: its link is under the module's base, lit by the location inside the module. */
  const entry = (item: RailItem) => item.inert || !item.to ? (
    <span key={item.title} className="rail-item" style={{ color: 'var(--ink-faint)', cursor: 'default' }} title={item.title}>
      {item.icon}{label(item)}
    </span>
  ) : (
    // A section stays lit while you are anywhere inside it, or on a page under it, which is what the navigation is for.
    <NavLink
      key={item.to}
      to={item.to}
      // a function, not a string: given a string, the router adds its own `active` by prefix, which lights a server's root on all its pages
      className={() => `rail-item ${isEntryActive(here, item) ? 'active' : ''} ${item.problem ? 'unreachable' : ''}`}
      title={tip(item)}
      aria-label={item.title}
      data-testid={item.testId}
      data-reachable={item.serverId ? String(!item.problem) : undefined}
    >
      {item.icon}{label(item)}
    </NavLink>
  )

  /** One of the host's entries: its link is the host's own address, lit by the whole location; the open server's entries follow its own. */
  const hostEntry = (item: RailItem) => (
    <div key={item.to ?? item.title} style={{ display: 'contents' }}>
      <RouterNavLink
        to={item.to ?? '/'}
        end={item.to === '/'}
        className={() => `rail-item ${isEntryActive(pathname, item) ? 'active' : ''} ${item.problem ? 'unreachable' : ''}`}
        title={tip(item)}
        aria-label={item.title}
        data-testid={item.testId && ownIds.has(item.testId) ? item.testId.replace(/^rail-/, 'rail-console-') : item.testId}
        data-reachable={item.serverId ? String(!item.problem) : undefined}
      >
        {item.icon}{label(item)}
      </RouterNavLink>
      {host && item.serverId === host.serverId && (
        <div className="rail-nested" data-testid="nav-server-entries">
          {own.map((group, i) => (
            <div key={group.title ?? `own-${i}`} className="rail-group" data-testid={groupTestId(group)}>
              {group.title && (collapsed ? <div className="rail-group-rule" aria-hidden /> : <div className="rail-group-title nested">{group.title}</div>)}
              {group.items.map(entry)}
            </div>
          ))}
        </div>
      )}
    </div>
  )

  return (
    <>
      <nav className={`rail ${collapsed ? 'collapsed' : 'wide'}`} data-testid="nav" data-collapsed={collapsed}>
        <div className="rail-mark">{mark}</div>
        {/* A module shown without a host's navigation but with a way back to it: that way comes first; its link is not under the module's base. */}
        {home && !host && (
          <RouterNavLink to={home.to} end className="rail-item" title={home.title} aria-label={home.title} data-testid="rail-home-link">
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden><path d="M15 6l-6 6 6 6" /></svg>
            {!collapsed && <span className="rail-label">{home.title}</span>}
          </RouterNavLink>
        )}
        {groups.map((group, i) => (
          <div key={group.title ?? `group-${i}`} className="rail-group" data-testid={groupTestId(group)}>
            {group.title && (collapsed ? <div className="rail-group-rule" aria-hidden /> : <div className="rail-group-title">{group.title}</div>)}
            {group.items.map(host ? hostEntry : entry)}
          </div>
        ))}
        <div className="rail-spacer" />
        <button type="button" className="rail-item rail-toggle" onClick={toggle} aria-expanded={!collapsed} title={collapsed ? 'Show the names' : 'Collapse to icons'} data-testid="nav-toggle">
          <ChevronIcon left={!collapsed} />{!collapsed && <span className="rail-label">Collapse</span>}
        </button>
      </nav>

      <div className="app">
        <header className="topbar">
          <span className="brand">{brand}</span>
          {/* Inside a host, which server this is: there can be several of a product. */}
          {host && (
            <>
              <span className="sep">/</span>
              <NavLink to="/" end className="crumb mono" data-testid="crumb-server">{host.serverName}</NavLink>
            </>
          )}
          {crumbs.map((crumb, i) => (
            <span key={`${crumb.label}-${i}`} style={{ display: 'contents' }}>
              <span className="sep">/</span>
              {crumb.to ? (
                <NavLink to={crumb.to} className={`crumb ${crumb.mono ? 'mono' : ''}`}>{crumb.label}</NavLink>
              ) : crumb.heading ? (
                <h1 className={`crumb here ${crumb.mono ? 'mono' : ''}`}>{crumb.label}</h1>
              ) : (
                <span className={`crumb here ${crumb.mono ? 'mono' : ''}`}>{crumb.label}</span>
              )}
            </span>
          ))}
          {/* Who is signed in, on every screen — it belongs to the shell rather than to whichever
              page remembered to render it. Absent entirely where the deployment does not
              authenticate. */}
          <div className="right">
            {actions}
            {/* Beside the signed-in identity, on every screen and for the same reason: a
                notification is about the deployment, not about whichever page happens to be open. */}
            <NotificationBell />
            <SignedInAs />
          </div>
        </header>

        {tabs && <div className="tabbar">{tabs}</div>}

        <div className="content">
          {children}
        </div>
      </div>
    </>
  )
}

export interface Crumb {
  label: string
  /** Omit for the current location — it renders as the leaf rather than a link. */
  to?: string
  /** Identifiers are set in the monospace face throughout the design. */
  mono?: boolean
  /**
   * Render this crumb as the page's `<h1>`. Set it only where the pane has no title of its own — the
   * replication detail screens, whose subject is named in the breadcrumb and nowhere else. A list
   * screen already has a heading in its pane, and two `<h1>`s reading the same word is worse than
   * none for anyone navigating by headings.
   */
  heading?: boolean
}
