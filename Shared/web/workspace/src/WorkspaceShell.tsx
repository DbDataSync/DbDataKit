import type { ReactNode } from 'react'
import { Shell, type Crumb, type RailItem } from '@dbdatakit/shell'

const icon = (path: ReactNode) => (
  <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden>{path}</svg>
)

const rail: RailItem[] = [
  { to: '/', title: 'Workspaces', testId: 'rail-workspaces', icon: icon(<><path d="M4 5h16v14H4z" /><path d="M4 9h16" /><path d="M8 13h4" /></>) },
  { to: '/keys', title: 'Publisher keys', testId: 'rail-keys', icon: icon(<><circle cx="8" cy="12" r="3" /><path d="M11 12h9M17 12v3M14 12v2" /></>) },
]

const Mark = () => icon(<><circle cx="6" cy="6" r="2" /><circle cx="6" cy="18" r="2" /><circle cx="18" cy="9" r="2" /><path d="M6 8v8M18 11c0 4-6 3-12 5" /></>)

/** DbDataWorkspace's rail inside a host. */
export function WorkspaceShell(props: { crumbs: Crumb[]; tabs?: ReactNode; actions?: ReactNode; children: ReactNode }) {
  return <Shell brand="DbDataWorkspace" mark={<Mark />} rail={rail} {...props} />
}
