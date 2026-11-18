import type { ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Shell, type Crumb, type NavGroup, type RailItem } from '@dbdatakit/shell'
import { consoleApi, instancesKey, type InstanceSummary } from './api'

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
const LineageIcon = () => (
  <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden>
    <circle cx="5" cy="12" r="2" /><circle cx="19" cy="6" r="2" /><circle cx="19" cy="18" r="2" /><path d="M7 12h4l6-5.5" /><path d="M11 12l6 5.5" />
  </svg>
)
const QualityIcon = () => (
  <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden>
    <path d="M12 3l8 3v6c0 4.5-3.2 7.8-8 9-4.8-1.2-8-4.5-8-9V6z" /><path d="M8.5 12l2.5 2.5 4.5-5" />
  </svg>
)
const AgentsIcon = () => (
  <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden>
    <rect x="5" y="8" width="14" height="11" rx="2.5" /><path d="M12 4v4M9 13h.01M15 13h.01M9.5 16.5h5" /><circle cx="12" cy="3.5" r="1" />
  </svg>
)
const KnowledgeIcon = () => (
  <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden>
    <path d="M5 4h10a3 3 0 013 3v13H8a3 3 0 01-3-3z" /><path d="M5 17a3 3 0 013-3h10M9 8h5" />
  </svg>
)
const BacklogIcon = () => (
  <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden>
    <rect x="4" y="4" width="5" height="16" rx="1" /><rect x="10" y="4" width="5" height="10" rx="1" /><rect x="16" y="4" width="4" height="13" rx="1" />
  </svg>
)
const DocsIcon = () => (
  <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden>
    <path d="M4 5.5C4 4.7 4.7 4 5.5 4H12v16H5.5C4.7 20 4 19.3 4 18.5z" /><path d="M20 5.5c0-.8-.7-1.5-1.5-1.5H12v16h6.5c.8 0 1.5-.7 1.5-1.5z" />
  </svg>
)
const icon = (path: ReactNode) => (
  <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden>{path}</svg>
)
const RunsIcon = () => icon(<><circle cx="12" cy="12" r="8.5" /><path d="M10 8.5l5 3.5-5 3.5z" /></>)
const TrackingIcon = () => icon(<><path d="M4 4v16h16" /><path d="M7 15l4-4 3 3 5-6" /></>)
const ApprovalsIcon = () => icon(<><rect x="5" y="4" width="14" height="17" rx="2" /><path d="M9 4V3h6v1" /><path d="M9 13l2 2 4-4" /></>)
const SetupIcon = () => icon(<><path d="M9 6h11M9 12h11M9 18h11" /><path d="M3.5 6l1 1 2-2M3.5 12l1 1 2-2M3.5 18l1 1 2-2" /></>)
const DeploymentsIcon = () => icon(<><path d="M12 3v12" /><path d="M7 10l5 5 5-5" /><path d="M4 19h16" /></>)
const ReleasesIcon = () => icon(<><path d="M3 12V4h8l10 10-8 8z" /><circle cx="7.5" cy="8" r="1.3" /></>)
const DeliveryIcon = () => icon(<><path d="M3 11l18-8-8 18-2-8z" /><path d="M11 13l10-10" /></>)
const BindingsIcon = () => icon(<><path d="M9 15l6-6" /><path d="M11 6l1.5-1.5a4 4 0 015.7 5.7L16.5 12" /><path d="M13 18l-1.5 1.5a4 4 0 01-5.7-5.7L7.5 12" /></>)
const AuditIcon = () => icon(<><circle cx="11" cy="11" r="6" /><path d="M20 20l-4.5-4.5" /><path d="M8.5 11h5M11 8.5v5" /></>)
const SecretsIcon = () => icon(<><rect x="5" y="11" width="14" height="9" rx="2" /><path d="M8 11V8a4 4 0 018 0v3" /><path d="M12 15v2" /></>)

const ProjectsIcon = () => icon(<><path d="M3 7a2 2 0 012-2h4l2 2h8a2 2 0 012 2v8a2 2 0 01-2 2H5a2 2 0 01-2-2z" /></>)
const GatewayIcon = () => icon(<><path d="M3 7l9-4 9 4-9 4-9-4z" /><path d="M3 12l9 4 9-4" /><path d="M3 17l9 4 9-4" /></>)
const RunnerIcon = () => icon(<><path d="M6 4l13 8-13 8z" /></>)
const WorkspaceIcon = () => icon(<><circle cx="6" cy="6" r="2" /><circle cx="6" cy="18" r="2" /><circle cx="18" cy="9" r="2" /><path d="M6 8v8M18 11c0 4-6 3-12 5" /></>)
const IdentityIcon = () => icon(<><circle cx="12" cy="8" r="3.5" /><path d="M5 20c.7-3.8 3.5-6 7-6s6.3 2.2 7 6" /></>)

const Mark = () => (
  <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden>
    <ellipse cx="12" cy="6" rx="8" ry="3" /><path d="M4 6v6c0 1.7 3.6 3 8 3s8-1.3 8-3V6" /><path d="M4 12v6c0 1.7 3.6 3 8 3s8-1.3 8-3v-6" />
  </svg>
)

/**
 * The console's navigation: Home, six groups named for what a person is doing, and Docs (administrators' entries are hidden from a viewer). A page
 * reached from an entry, with no entry of its own, is listed under it in `pages`, so the entry stays lit there.
 *
 * Design: DbDataKit/README.md#finding-a-screen-the-navigation
 */
export const consoleNav: NavGroup[] = [
  { items: [{ to: '/', title: 'Home', icon: <HomeIcon />, testId: 'rail-home' }] },
  {
    title: 'Explore',
    items: [
      { to: '/views', title: 'Views', icon: <ViewsIcon />, testId: 'rail-views' },
      { to: '/lineage', title: 'Lineage', icon: <LineageIcon />, testId: 'rail-lineage' },
      { to: '/quality', title: 'Quality', icon: <QualityIcon />, testId: 'rail-quality' },
    ],
  },
  {
    title: 'Build and release',
    items: [
      { to: '/releases', title: 'Releases', icon: <ReleasesIcon />, testId: 'rail-releases' },
      { to: '/deployments', title: 'Deployments', icon: <DeploymentsIcon />, testId: 'rail-deployments' },
      { to: '/backlog', title: 'Backlog', icon: <BacklogIcon />, testId: 'rail-backlog' },
    ],
  },
  {
    title: 'Operate',
    items: [
      { to: '/monitoring', title: 'Monitoring', icon: <MonitoringIcon />, testId: 'rail-monitoring' },
      { to: '/runs', title: 'Runs', icon: <RunsIcon />, testId: 'rail-runs' },
      { to: '/approvals', title: 'Approvals', icon: <ApprovalsIcon />, testId: 'rail-approvals' },
      { to: '/delivery', title: 'Delivery', icon: <DeliveryIcon />, testId: 'rail-delivery' },
      { to: '/tracking', title: 'Tracking', icon: <TrackingIcon />, testId: 'rail-tracking' },
    ],
  },
  {
    title: 'Agents and knowledge',
    items: [
      { to: '/agents', title: 'Agents', icon: <AgentsIcon />, testId: 'rail-agents' },
      { to: '/knowledge', title: 'Knowledge', icon: <KnowledgeIcon />, testId: 'rail-knowledge' },
    ],
  },
  {
    title: 'Servers',
    items: [
      { to: '/instances', title: 'All servers', icon: <ServersIcon />, testId: 'rail-instances', adminOnly: true, pages: ['/upgrades', '/state'] },
    ],
  },
  {
    title: 'Settings',
    items: [
      { to: '/connections', title: 'Connections', icon: <ConnectionsIcon />, testId: 'rail-connections-all' },
      { to: '/bindings', title: 'Bindings', icon: <BindingsIcon />, testId: 'rail-bindings' },
      { to: '/secrets', title: 'Secrets', icon: <SecretsIcon />, testId: 'rail-secrets', adminOnly: true },
      { to: '/users', title: 'Users', icon: <UsersIcon />, testId: 'rail-users', adminOnly: true },
      { to: '/audit', title: 'Audit', icon: <AuditIcon />, testId: 'rail-audit', adminOnly: true },
      { to: '/setup', title: 'Setup', icon: <SetupIcon />, testId: 'rail-setup', adminOnly: true },
    ],
  },
  { items: [{ to: '/docs', title: 'Docs', icon: <DocsIcon />, testId: 'rail-docs' }] },
]

/** What a paired server's product is called in the navigation, beside its name. */
const productWord: Record<string, string> = { dbdatagateway: 'Gateway', dbdatarunner: 'Runner', dbdataworkspace: 'Workspace', 'dbdatakit-identity': 'Identity home' }
const productIcon: Record<string, () => ReactNode> = { dbdatagateway: GatewayIcon, dbdatarunner: RunnerIcon, dbdataworkspace: WorkspaceIcon, 'dbdatakit-identity': IdentityIcon }

/**
 * The console's navigation with the paired servers in it: each server under Servers by its name and product (marked when it does not answer), and
 * Projects under Build and release for each paired Workspace (named for the Workspace when there are several). A server's entry opens its screens,
 * whose own entries are then nested under it.
 */
export function navWithServers(servers: InstanceSummary[]): NavGroup[] {
  const sorted = [...servers].sort((a, b) => a.name.localeCompare(b.name))
  const workspaces = sorted.filter((s) => s.product === 'dbdataworkspace')
  const projects: RailItem[] = workspaces.map((w) => workspaces.length === 1
    ? { to: `/servers/${w.id}`, title: 'Projects', icon: <ProjectsIcon />, testId: 'rail-projects' }
    : { to: `/servers/${w.id}`, title: `Projects on ${w.name}`, icon: <ProjectsIcon />, testId: `rail-projects-${w.id}` })
  const serverEntries: RailItem[] = sorted.map((s) => {
    const Icon = productIcon[s.product ?? ''] ?? ServersIcon
    return {
      to: `/servers/${s.id}`, title: s.name, icon: <Icon />, testId: `nav-server-${s.id}`, serverId: s.id,
      note: s.reachable ? productWord[s.product ?? ''] ?? s.product ?? undefined : 'not answering',
      problem: s.reachable ? undefined : `not answering: ${s.problem ?? 'no answer'}`,
    }
  })
  return consoleNav.map((group) =>
    group.title === 'Build and release' ? { ...group, items: [...projects, ...group.items] }
      : group.title === 'Servers' ? { ...group, items: [...group.items, ...serverEntries] }
        : group)
}

/** The console's navigation as this person may see it: with the paired servers when the console lists them (a person it refuses sees none). */
export function useConsoleNav(): NavGroup[] {
  const servers = useQuery({ queryKey: instancesKey, queryFn: consoleApi.instances.list, staleTime: 30_000, retry: false })
  return navWithServers(servers.data ?? [])
}

export function ConsoleShell(props: { crumbs: Crumb[]; tabs?: ReactNode; actions?: ReactNode; children: ReactNode }) {
  return <Shell brand={PRODUCT} mark={<Mark />} rail={useConsoleNav()} {...props} />
}
