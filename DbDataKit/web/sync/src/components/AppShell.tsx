import type { ReactNode } from 'react'
import { Shell, type Crumb, type RailItem } from '@dbdatakit/shell'
import { BookIcon, CodeIcon, DatabaseIcon, FlowIcon, GearIcon, GridIcon, LogoIcon, PackageIcon } from './icons'

export type { Crumb }

/**
 * DbDataSync's rail: what the shell (`@dbdatakit/shell`) frames every screen with. Drivers, Libraries and Files are used by whoever configures a replication
 * but only an admin changes what is installed on the host at all; Admin is the section root, so the item stays lit on any tab inside it. The API enforces
 * the role for real (Policies.Admin on every endpoint underneath); hiding an item only avoids offering a viewer a destination that 403s on arrival.
 */
const rail: RailItem[] = [
  { to: '/replications', title: 'Replications', icon: <FlowIcon />, testId: 'rail-replications' },
  { to: '/connections', title: 'Connections', icon: <DatabaseIcon />, testId: 'rail-connections' },
  { to: '/scripts', title: 'Scripts', icon: <CodeIcon />, testId: 'rail-scripts' },
  { to: '/drivers', title: 'Drivers', icon: <PackageIcon />, testId: 'rail-drivers', adminOnly: true },
  { title: 'Overview', icon: <GridIcon />, inert: true },
  { to: '/docs', title: 'Docs', icon: <BookIcon />, testId: 'rail-docs' },
  { to: '/admin', title: 'Admin', icon: <GearIcon />, testId: 'rail-admin', adminOnly: true },
]

export function AppShell(props: { crumbs: Crumb[]; tabs?: ReactNode; actions?: ReactNode; children: ReactNode }) {
  return <Shell brand="DbDataSync" mark={<LogoIcon />} rail={rail} {...props} />
}
