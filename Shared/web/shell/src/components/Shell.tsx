import type { ReactNode } from 'react'
import { NavLink as RouterNavLink } from 'react-router-dom'
import { NavLink, useModule } from '../module'
import { NotificationBell } from './NotificationBell'
import { SignedInAs } from './SignIn'
import { useIsAdmin } from './useIsAdmin'

/** One destination in the icon rail. `inert` draws the icon with nothing behind it (a placeholder the product has not built yet). */
export interface RailItem {
  /** The route; a section stays lit while the location is anywhere inside it. Omit for an `inert` item. */
  to?: string
  title: string
  icon: ReactNode
  /** `data-testid` of the link. */
  testId?: string
  /** Shown only to an admin. The API enforces the role for real; hiding the item only avoids offering a viewer a destination that would 403 on arrival. */
  adminOnly?: boolean
  inert?: boolean
}

/**
 * The chrome every screen sits in: a 46px icon rail, a 42px breadcrumb bar and a 46px tab bar. The product says what is in it: its name, its mark and its rail
 * (plan 13, U02); the signed-in identity and the notification bell are the shell's own, on every screen, absent where the deployment does not authenticate.
 *
 * The mockups also show an environment pill, an Activity tab and a health roll-up in this bar. None of those have anything behind them, and a console that displays an
 * invented reading is worse than one that displays none.
 */
export function Shell({ brand, mark, rail, crumbs, tabs, actions, children }: {
  /** The product's name, first in the breadcrumb bar. */
  brand: string
  /** The product's mark, at the top of the rail. */
  mark: ReactNode
  rail: RailItem[]
  crumbs: Crumb[]
  tabs?: ReactNode
  actions?: ReactNode
  children: ReactNode
}) {
  const isAdmin = useIsAdmin()
  const { home } = useModule()

  return (
    <>
      <nav className="rail">
        <div className="rail-mark">{mark}</div>
        {/* Inside the console, the way back to it comes first; its link is not under the module's base. */}
        {home && (
          <RouterNavLink to={home.to} end className="rail-item" title={home.title} data-testid="rail-home-link">
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden><path d="M15 6l-6 6 6 6" /></svg>
          </RouterNavLink>
        )}
        {rail.filter(item => !item.adminOnly || isAdmin).map(item => item.inert || !item.to ? (
          <span key={item.title} className="rail-item" style={{ color: 'var(--ink-faint)', cursor: 'default' }} title={item.title}>
            {item.icon}
          </span>
        ) : (
          // Not `end`: a section stays lit while you are anywhere inside it, which is what the rail is for.
          <NavLink
            key={item.to}
            to={item.to}
            className={({ isActive }) => `rail-item ${isActive ? 'active' : ''}`}
            title={item.title}
            data-testid={item.testId}
          >
            {item.icon}
          </NavLink>
        ))}
      </nav>

      <div className="app">
        <header className="topbar">
          <span className="brand">{brand}</span>
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
