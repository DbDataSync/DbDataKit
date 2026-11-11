import { lazy, Suspense } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useParams } from 'react-router-dom'
import { ModuleProvider } from '@dbdatakit/shell'
import { consoleApi } from '../api'
import { ConsoleShell } from '../ConsoleShell'

// The Sync screens are large (an editor, a grid, a realtime client); a console that is only showing its servers does not load them.
const SyncModule = lazy(() => import('@dbdatakit/sync').then((m) => ({ default: m.SyncModule })))
const BuildModule = lazy(() => import('@dbdatakit/build').then((m) => ({ default: m.BuildModule })))
const GatewayModule = lazy(() => import('@dbdatakit/gateway').then((m) => ({ default: m.GatewayModule })))
const IdentityHomeModule = lazy(() => import('@dbdatakit/gateway').then((m) => ({ default: m.IdentityHomeModule })))
const RunnerModule = lazy(() => import('@dbdatakit/runner').then((m) => ({ default: m.RunnerModule })))
const StoreModule = lazy(() => import('@dbdatakit/store').then((m) => ({ default: m.StoreModule })))
const WorkspaceModule = lazy(() => import('@dbdatakit/workspace').then((m) => ({ default: m.WorkspaceModule })))

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
  const Module = found.product === 'dbdatasync' ? SyncModule : found.product === 'dbdatabuild' ? BuildModule : found.product === 'dbdatagateway' ? GatewayModule : found.product === 'dbdatastore' ? StoreModule : found.product === 'dbdatarunner' ? RunnerModule : found.product === 'dbdataworkspace' ? WorkspaceModule : found.product === 'dbdatakit-identity' ? IdentityHomeModule : null
  if (Module === null) return <Notice testId="module-none">{`The console has no screens for ${found.product ?? 'this kind of server'} yet.`}</Notice>

  return (
    <ModuleProvider info={{ basePath: `/servers/${found.id}`, apiBase: `/relay/${found.id}`, home: { to: '/instances', title: 'Servers' } }}>
      <Suspense fallback={null}>
        <Module />
      </Suspense>
    </ModuleProvider>
  )
}
