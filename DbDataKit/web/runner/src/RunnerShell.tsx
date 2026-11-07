import type { ReactNode } from 'react'
import { Shell, type Crumb, type RailItem } from '@dbdatakit/shell'

const icon = (path: ReactNode) => (
  <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden>{path}</svg>
)

const rail: RailItem[] = [
  { to: '/', title: 'Tasks', testId: 'rail-tasks', icon: icon(<><circle cx="12" cy="12" r="8" /><path d="M12 8v4l3 2" /></>) },
  { to: '/runs', title: 'Runs', testId: 'rail-runs', icon: icon(<><path d="M5 6h14M5 12h14M5 18h9" /></>) },
  { to: '/packages', title: 'Packages', testId: 'rail-packages', icon: icon(<><path d="M4 8l8-4 8 4v8l-8 4-8-4z" /><path d="M4 8l8 4 8-4M12 12v8" /></>) },
]

const Mark = () => icon(<><path d="M5 4l14 8-14 8z" /></>)

/** DbDataRunner's rail inside a host. */
export function RunnerShell(props: { crumbs: Crumb[]; tabs?: ReactNode; actions?: ReactNode; children: ReactNode }) {
  return <Shell brand="DbDataRunner" mark={<Mark />} rail={rail} {...props} />
}
