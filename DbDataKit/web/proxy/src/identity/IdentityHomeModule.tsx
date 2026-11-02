import type { ReactNode } from 'react'
import { Navigate, Route, Routes } from 'react-router-dom'
import { Shell, type Crumb, type RailItem } from '@dbdatakit/shell'
import { IdentityAdminPage } from './IdentityAdminPage'

const icon = (path: ReactNode) => (
  <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden>{path}</svg>
)

const rail: RailItem[] = [
  { to: '/', title: 'Identity home', testId: 'rail-identity-home', icon: icon(<><circle cx="12" cy="8" r="3.5" /><path d="M5 20c.7-3.8 3.5-6 7-6s6.3 2.2 7 6" /></>) },
]

function IdentityShell(props: { crumbs: Crumb[]; children: ReactNode }) {
  return <Shell brand="Identity home" mark={icon(<><circle cx="12" cy="8" r="3.5" /><path d="M5 20c.7-3.8 3.5-6 7-6s6.3 2.2 7 6" /></>)} rail={rail} {...props} />
}

/** The screens of an identity home that runs on its own (`dbdatakit identity run`, plan 13a U21), mounted in the console under the server's address. */
export function IdentityHomeModule() {
  return (
    <Routes>
      <Route path="/" element={<IdentityAdminPage Frame={IdentityShell} />} />
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  )
}
