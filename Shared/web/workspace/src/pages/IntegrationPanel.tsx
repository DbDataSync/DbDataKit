import { useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { ErrorBanner, SchemaSet } from '@dbdatakit/shell'
import { workspaceApi, type CheckDiagnostic } from '../api'
import { liveState } from '../editors'
import { INTEGRATION_MANIFEST_SCHEMA, MANIFEST_PATH, integrationFilesOf, integrationManifestSchema, newTaskFiles, taskNameProblem, tasksOf } from '../integrationSchemas'
import type { LiveCheck } from '../useLiveCheck'
import { StructuredEditor } from './StructuredPanel'
import { CheckFailedNote, findingsKey } from './SyncSetPanel'

const schemas = new SchemaSet({ [INTEGRATION_MANIFEST_SCHEMA]: integrationManifestSchema })

/** The manifest of an integration as a form (from the schema) or as YAML, with the check of the draft beside it: the Runner's own reader and the compile are what the check answers with. */
export function IntegrationManifestEditor({ path, text, onText, readOnly, live, onOpen }: { path: string; text: string; onText: (text: string) => void; readOnly: boolean; live: LiveCheck; onOpen?: (path: string) => void }) {
  const state = liveState({ text, checkedText: live.checkedText, failed: live.failed, running: live.running })
  const notes = (
    <>
      <div className="hint" style={{ marginTop: 10 }} data-testid="wsp-int-slots-note">
        A connection slot is bound to a connection of the channel on the Runner, so the code never holds a secret; a connection names its secret by reference (<code>dbdatakit:&lt;service&gt;:&lt;kind&gt;:&lt;name&gt;</code>). Triggers and the progress declaration are in the YAML tab.
      </div>
      {live.failed && <div className="banner error" role="alert" style={{ marginTop: 10 }} data-testid="wsp-live-failed"><span className="mark">!</span><span>{live.failed}</span></div>}
    </>
  )
  return <StructuredEditor schemas={schemas} schema={INTEGRATION_MANIFEST_SCHEMA} path={path} text={text} onText={onText} readOnly={readOnly} diagnostics={live.diagnostics} state={state} summary="The package of this integration" notes={notes} onOpen={onOpen} />
}

/**
 * The integration at a glance (plan 27a IX24): its tasks as the manifest lists them, the code files and the tests, each opening its file with the count of findings of the check, a form that adds a task together with its class and its test,
 * and the button that runs the tests (a build: the tests first, then the package; the Builds tab shows the result and publishes it).
 */
export function IntegrationSetPanel({ manifest, paths, canWrite, busy, onOpen, onCreate, findings, onRunTests, running }: {
  manifest: string | null; paths: readonly string[]; canWrite: boolean; busy: boolean; onOpen: (path: string) => void; onCreate: (files: { path: string; text: string }[]) => void
  findings?: readonly CheckDiagnostic[]; onRunTests: () => void; running: boolean
}) {
  const files = integrationFilesOf(paths)
  const tasks = manifest === null ? [] : tasksOf(manifest)
  const [name, setName] = useState('')
  const errorsIn = (path: string) => (findings ?? []).filter((d) => d.severity === 'error' && d.file === path).length
  const problem = taskNameProblem(name, tasks.map((t) => t.name))
  const ok = name !== '' && problem === null && !busy && manifest !== null
  const row = (path: string, testId: string) => {
    const bad = errorsIn(path)
    return (
      <div key={path} className="grid-row" style={{ gap: 8 }} data-testid={testId} data-path={path} data-errors={bad}>
        <button type="button" className="btn-link mono" data-testid={`${testId}-open`} onClick={() => onOpen(path)}>{path}</button>
        {bad > 0 && <span className="error">{bad} to fix</span>}
      </div>
    )
  }
  return (
    <div className="card" data-testid="wsp-integration">
      <div className="card-body">
        <div className="hint" style={{ marginBottom: 6 }}>Tasks <button type="button" className="btn-link mono" data-testid="wsp-int-open-manifest" onClick={() => onOpen(MANIFEST_PATH)}>{MANIFEST_PATH}</button></div>
        {tasks.length === 0 && <div className="hint" data-testid="wsp-int-no-tasks">{manifest === null ? 'The manifest could not be read.' : 'No task in the manifest yet.'}</div>}
        {tasks.map((t) => (
          <div key={t.name} className="grid-row" style={{ gap: 8, flexWrap: 'wrap' }} data-testid="wsp-int-task" data-name={t.name}>
            <span className="mono">{t.name}</span><span className="hint">{t.when}{t.slots > 0 ? `, ${t.slots} connection slot${t.slots === 1 ? '' : 's'}` : ''}</span>
          </div>
        ))}
        <div className="hint" style={{ margin: '10px 0 4px' }}>Code (C# files; a build needs no .NET SDK)</div>
        {files.code.length === 0 && <div className="hint" data-testid="wsp-int-no-code">No file in src/.</div>}
        {files.code.map((p) => row(p, 'wsp-int-code'))}
        <div className="hint" style={{ margin: '10px 0 4px' }}>Tests (a script passes when it finishes)</div>
        {files.tests.length === 0 && <div className="hint" data-testid="wsp-int-no-tests">No test yet; a build says so.</div>}
        {files.tests.map((p) => row(p, 'wsp-int-test'))}
        {canWrite && (
          <>
            <form className="row" style={{ gap: 6, marginTop: 10, flexWrap: 'wrap', alignItems: 'center' }} data-testid="wsp-new-task" onSubmit={(e) => { e.preventDefault(); if (ok) { onCreate(newTaskFiles(manifest!, name)); setName('') } }}>
              <input className="input mono" style={{ flex: 1, minWidth: 120 }} aria-label="New task name" placeholder="pull-orders" value={name} onChange={(e) => setName(e.target.value)} data-testid="wsp-new-task-name" />
              <button type="submit" className="btn" disabled={!ok} data-testid="wsp-new-task-go">New task</button>
              {problem && <span className="hint" data-testid="wsp-new-task-why">{problem}</span>}
            </form>
            <div className="hint" style={{ marginTop: 6 }}>A new task adds its entry to the manifest, a job class in <code>src/</code> and a test in <code>tests/</code>.</div>
          </>
        )}
        <div style={{ marginTop: 12 }}>
          <button type="button" className="btn btn-primary" disabled={!canWrite || running} data-testid="wsp-int-run-tests" onClick={onRunTests}>{running ? 'Starting...' : 'Run the tests'}</button>
          <span className="hint" style={{ marginLeft: 8 }}>This is a build: the tests first, then the package. Its result and the publish to a Runner are on the Builds tab.</span>
        </div>
      </div>
    </div>
  )
}

/** The integration with its manifest read, its findings counted and its tests started; every save invalidates the findings (`findingsKey`), so the counts follow the files. */
export function IntegrationOverview({ project, id, paths, canWrite, busy, onOpen, onCreate, onBuildStarted }: { project: string; id: string; paths: readonly string[]; canWrite: boolean; busy: boolean; onOpen: (path: string) => void; onCreate: (files: { path: string; text: string }[]) => void; onBuildStarted: () => void }) {
  const check = useQuery({ queryKey: findingsKey(project, id), queryFn: () => workspaceApi.check(project, id), staleTime: 30_000, retry: false })
  const manifest = useQuery({ queryKey: ['workspace', 'integration-manifest', project, id, paths.includes(MANIFEST_PATH), check.dataUpdatedAt], queryFn: () => workspaceApi.readFile(project, id, MANIFEST_PATH), enabled: paths.includes(MANIFEST_PATH), staleTime: 5_000, retry: false })
  const run = useMutation({ mutationFn: () => workspaceApi.requestBuild(project, id), onSuccess: onBuildStarted })
  return (
    <>
      <IntegrationSetPanel manifest={manifest.data ?? null} paths={paths} canWrite={canWrite} busy={busy} onOpen={onOpen} onCreate={onCreate} findings={check.data?.diagnostics} onRunTests={() => run.mutate()} running={run.isPending} />
      <CheckFailedNote error={check.error} />
      <ErrorBanner error={run.error} />
    </>
  )
}
