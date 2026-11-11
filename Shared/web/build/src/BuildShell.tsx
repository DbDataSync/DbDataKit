import type { ReactNode } from 'react'
import { Shell, type Crumb, type RailItem } from '@dbdatakit/shell'

const ProjectsIcon = () => (
  <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden>
    <rect x="3" y="3" width="7" height="7" rx="1" /><rect x="14" y="3" width="7" height="7" rx="1" /><rect x="3" y="14" width="7" height="7" rx="1" /><path d="M14 17.5h7M17.5 14v7" />
  </svg>
)
const Mark = () => (
  <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden>
    <path d="M4 20l8-16 8 16z" /><path d="M8 14h8" />
  </svg>
)

/** DbDataBuild's rail inside a host. Projects are the one section so far; the rest (plans, status, the editor) come with the slices that build them. */
const rail: RailItem[] = [{ to: '/', title: 'Projects', icon: <ProjectsIcon />, testId: 'rail-projects' }]

export function BuildShell(props: { crumbs: Crumb[]; tabs?: ReactNode; actions?: ReactNode; children: ReactNode }) {
  return <Shell brand="DbDataBuild" mark={<Mark />} rail={rail} {...props} />
}
