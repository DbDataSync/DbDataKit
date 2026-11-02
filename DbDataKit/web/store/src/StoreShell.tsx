import type { ReactNode } from 'react'
import { Shell, type Crumb, type RailItem } from '@dbdatakit/shell'

const icon = (path: ReactNode) => (
  <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden>{path}</svg>
)

const rail: RailItem[] = [
  { to: '/', title: 'Models', testId: 'rail-models', icon: icon(<><ellipse cx="12" cy="6" rx="8" ry="3" /><path d="M4 6v6c0 1.7 3.6 3 8 3s8-1.3 8-3V6" /><path d="M4 12v6c0 1.7 3.6 3 8 3s8-1.3 8-3v-6" /></>) },
]

const Mark = () => icon(<><path d="M4 5h16v14H4z" /><path d="M4 10h16M10 5v14" /></>)

/** DbDataStore's rail inside a host. */
export function StoreShell(props: { crumbs: Crumb[]; tabs?: ReactNode; actions?: ReactNode; children: ReactNode }) {
  return <Shell brand="DbDataStore" mark={<Mark />} rail={rail} {...props} />
}
