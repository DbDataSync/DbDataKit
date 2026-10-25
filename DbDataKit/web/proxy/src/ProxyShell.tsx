import type { ReactNode } from 'react'
import { Shell, type Crumb, type RailItem } from '@dbdatakit/shell'

const icon = (path: ReactNode) => (
  <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden>{path}</svg>
)

const rail: RailItem[] = [
  { to: '/', title: 'Overview', testId: 'rail-overview', icon: icon(<><path d="M3 12h4l3-8 4 16 3-8h4" /></>) },
  { to: '/data-sources', title: 'Data sources', testId: 'rail-data-sources', icon: icon(<><ellipse cx="12" cy="6" rx="8" ry="3" /><path d="M4 6v6c0 1.7 3.6 3 8 3s8-1.3 8-3V6" /><path d="M4 12v6c0 1.7 3.6 3 8 3s8-1.3 8-3v-6" /></>) },
  { to: '/identities', title: 'Users and sign-in', testId: 'rail-identities', icon: icon(<><circle cx="9" cy="8" r="3.5" /><path d="M2.5 20c.6-3.6 3.2-5.5 6.5-5.5s5.9 1.9 6.5 5.5" /><path d="M16 4.5a3.5 3.5 0 010 7" /></>) },
  { to: '/tls', title: 'Certificate', testId: 'rail-tls', icon: icon(<><rect x="5" y="11" width="14" height="9" rx="2" /><path d="M8 11V8a4 4 0 018 0v3" /></>) },
  { to: '/activity', title: 'Activity', testId: 'rail-activity', icon: icon(<><path d="M4 19V9M10 19V5M16 19v-7M22 19H2" /></>) },
  { to: '/config', title: 'Configuration history', testId: 'rail-config', icon: icon(<><circle cx="6" cy="6" r="2.5" /><circle cx="6" cy="18" r="2.5" /><circle cx="18" cy="12" r="2.5" /><path d="M6 8.5v7M8.5 6.5c5 0 7 2 7 5" /></>) },
]

const Mark = () => icon(<><path d="M3 7l9-4 9 4-9 4-9-4z" /><path d="M3 12l9 4 9-4" /><path d="M3 17l9 4 9-4" /></>)

/** DbDataProxy's rail inside a host. */
export function ProxyShell(props: { crumbs: Crumb[]; tabs?: ReactNode; actions?: ReactNode; children: ReactNode }) {
  return <Shell brand="DbDataProxy" mark={<Mark />} rail={rail} {...props} />
}
