import type { ReactNode } from 'react'
import { Shell, type Crumb, type RailItem } from '@dbdatakit/shell'

export const PRODUCT = 'dbdatakit'

const HomeIcon = () => (
  <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden>
    <path d="M3 11l9-8 9 8" /><path d="M5 10v10h14V10" />
  </svg>
)
const UsersIcon = () => (
  <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden>
    <circle cx="9" cy="8" r="3.5" /><path d="M2.5 20c.6-3.6 3.2-5.5 6.5-5.5s5.9 1.9 6.5 5.5" /><path d="M16 4.5a3.5 3.5 0 010 7" /><path d="M18 14.8c2 .7 3.2 2.4 3.5 5.2" />
  </svg>
)
const ServersIcon = () => (
  <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden>
    <rect x="3" y="4" width="18" height="6" rx="1.5" /><rect x="3" y="14" width="18" height="6" rx="1.5" /><path d="M7 7h.01M7 17h.01" />
  </svg>
)
const ConnectionsIcon = () => (
  <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden>
    <circle cx="6" cy="12" r="2.5" /><circle cx="18" cy="6" r="2.5" /><circle cx="18" cy="18" r="2.5" /><path d="M8.3 11l7.4-3.7M8.3 13l7.4 3.7" />
  </svg>
)
const MonitoringIcon = () => (
  <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden>
    <path d="M3 12h4l3-8 4 16 3-8h4" />
  </svg>
)
const ViewsIcon = () => (
  <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden>
    <path d="M4 20V4" /><path d="M4 20h16" /><rect x="7.5" y="11" width="3" height="6" /><rect x="13" y="7" width="3" height="10" />
  </svg>
)
const DocsIcon = () => (
  <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden>
    <path d="M4 5.5C4 4.7 4.7 4 5.5 4H12v16H5.5C4.7 20 4 19.3 4 18.5z" /><path d="M20 5.5c0-.8-.7-1.5-1.5-1.5H12v16h6.5c.8 0 1.5-.7 1.5-1.5z" />
  </svg>
)
const Mark = () => (
  <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden>
    <ellipse cx="12" cy="6" rx="8" ry="3" /><path d="M4 6v6c0 1.7 3.6 3 8 3s8-1.3 8-3V6" /><path d="M4 12v6c0 1.7 3.6 3 8 3s8-1.3 8-3v-6" />
  </svg>
)

/** The console's rail: where the people who sign in are managed, and (in the slices that follow) the servers they manage. */
const rail: RailItem[] = [
  { to: '/', title: 'Home', icon: <HomeIcon />, testId: 'rail-home' },
  { to: '/instances', title: 'Servers', icon: <ServersIcon />, testId: 'rail-instances', adminOnly: true },
  { to: '/connections', title: 'Connections', icon: <ConnectionsIcon />, testId: 'rail-connections-all' },
  { to: '/monitoring', title: 'Monitoring', icon: <MonitoringIcon />, testId: 'rail-monitoring' },
  { to: '/views', title: 'Views', icon: <ViewsIcon />, testId: 'rail-views' },
  { to: '/docs', title: 'Docs', icon: <DocsIcon />, testId: 'rail-docs' },
  { to: '/users', title: 'Users', icon: <UsersIcon />, testId: 'rail-users', adminOnly: true },
]

export function ConsoleShell(props: { crumbs: Crumb[]; tabs?: ReactNode; actions?: ReactNode; children: ReactNode }) {
  return <Shell brand={PRODUCT} mark={<Mark />} rail={rail} {...props} />
}
