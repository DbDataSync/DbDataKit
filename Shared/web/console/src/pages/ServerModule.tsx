import { lazy, Suspense } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useParams } from 'react-router-dom'
import { ModuleProvider } from '@dbdatakit/shell'
import { consoleApi } from '../api'
import { ConsoleShell } from '../ConsoleShell'

const GatewayModule = lazy(() => import('@dbdatakit/gateway').then((m) => ({ default: m.GatewayModule })))
const IdentityHomeModule = lazy(() => import('@dbdatakit/gateway').then((m) => ({ default: m.IdentityHomeModule })))
const RunnerModule = lazy(() => import('@dbdatakit/runner').then((m) => ({ default: m.RunnerModule })))
const WorkspaceModule = lazy(() => import('@dbdatakit/workspace').then((m) => ({ default: m.WorkspaceModule })))

/** The servers plan 33 SC23 deleted, with where their work went: a console that still lists one says so instead of "no screens yet". */
export const removedProducts: Record<string, string> = {
  dbdatabuild: 'The Build server was deleted (plan 33 SC23): a project is authored in a build project of the Workspace and published from there to a Runner and a Gateway; its refresh plans run on the Runner, and a plan is applied, a change acknowledged or seeds loaded as the manual Runner tasks apply_plan, ack and seed, with the logins bound on the Runner.',
  dbdatasync: 'The Sync server was deleted (plan 33 SC23): a replication is authored in a sync project of the Workspace and published from there to a Runner, which runs it (run now, pause and resume, bulk loads, verifications and the provision task are Runner tasks), with the logins bound on the Runner.',
  dbdatastore: 'The Store server was deleted (plan 33 SC23): a model is authored in a store project of the Workspace, its tables are deployed from the Deploy tab there as Runner tasks, its rows are written on the Rows tab against a Gateway, and change requests are decided on the Approvals page.',
}

/** What a person sees while a module loads or when there is none for this server. */
function Notice({ children, testId }: { children: string; testId: string }) {
  return (
    <ConsoleShell crumbs={[{ label: 'Servers', to: '/instances' }]}>
      <div className="pane"><div className="empty" data-testid={testId}>{children}</div></div>
    </ConsoleShell>
  )
}

/**
 * The screens of the product a server runs (plan 13, U08), mounted under the server's own address in the console: its links keep working under `/servers/<id>/<product>`, its `/api` calls go to the console's relay
 * for this server, and it has a cache of its own. Which module is chosen by what the server says it is.
 */
export function ServerModule() {
  const { instance = '' } = useParams()
  const instances = useQuery({ queryKey: ['instances'], queryFn: consoleApi.instances.list })
  const found = (instances.data ?? []).find((i) => i.id === instance)

  if (instances.isLoading) return null
  if (!found) return <Notice testId="module-no-server">That server is not paired with this console.</Notice>
  if (!found.reachable) return <Notice testId="module-unreachable">{`${found.name} is not answering: ${found.problem ?? 'no answer'}.`}</Notice>
  const Module = found.product === 'dbdatagateway' ? GatewayModule : found.product === 'dbdatarunner' ? RunnerModule : found.product === 'dbdataworkspace' ? WorkspaceModule : found.product === 'dbdatakit-identity' ? IdentityHomeModule : null
  if (found.product && removedProducts[found.product]) return <Notice testId="module-removed">{removedProducts[found.product]}</Notice>
  if (Module === null) return <Notice testId="module-none">{`The console has no screens for ${found.product ?? 'this kind of server'} yet.`}</Notice>

  return (
    <ModuleProvider info={{ basePath: `/servers/${found.id}`, apiBase: `/relay/${found.id}`, home: { to: '/instances', title: 'Servers' } }}>
      <Suspense fallback={null}>
        <Module />
      </Suspense>
    </ModuleProvider>
  )
}
