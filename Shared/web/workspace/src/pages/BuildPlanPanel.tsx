import { useMutation, useQuery } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'
import { workspaceApi, type CheckDiagnostic } from '../api'
import { findingsOfModel, modelsOf, planFilesOf, planSummary, planWord, type ModelEntry, type PlanConnection } from '../buildPlan'
import { CheckResultView } from './CheckPanel'

/** The models of a Build project by folder, each opening its files, with the errors and warnings the last check found in them. Drawn without a query so a test can render it. */
export function ModelsView({ models, diagnostics, onOpen }: { models: readonly ModelEntry[]; diagnostics: readonly CheckDiagnostic[]; onOpen: (path: string) => void }) {
  const folders = [...new Set(models.map((m) => m.folder))]
  return (
    <div data-testid="wsp-build-models">
      {models.length === 0 && <div className="empty" data-testid="wsp-build-no-models">No model yet: a model is <code>models/&lt;folder&gt;/&lt;name&gt;.sql</code> with a <code>.yml</code> beside it.</div>}
      {folders.map((f) => (
        <div key={f} data-testid="wsp-build-folder" data-folder={f}>
          <div className="hint mono" style={{ margin: '8px 0 2px' }}>models/{f}</div>
          {models.filter((m) => m.folder === f).map((m) => {
            const found = findingsOfModel(m, diagnostics)
            return (
              <div key={m.name} className="grid-row" style={{ gap: 10, flexWrap: 'wrap' }} data-testid="wsp-build-model" data-name={m.name} data-errors={found.errors}>
                <span className="mono">{m.name}</span>
                {m.files.map((p) => <button key={p} type="button" className="btn-link mono hint" data-testid="wsp-build-model-file" onClick={() => onOpen(p)}>{p.split('.').pop()}</button>)}
                {found.errors > 0 && <span className="error">{found.errors} to fix</span>}
                {found.errors === 0 && found.warnings > 0 && <span className="hint">{found.warnings} warning{found.warnings === 1 ? '' : 's'}</span>}
              </div>
            )
          })}
        </div>
      ))}
    </div>
  )
}

/** The compiled plan: per connection the loads file and each refresh plan, opening its file; a plan says how many loads it runs when its text was read. */
export function PlanView({ plans, texts, onOpen }: { plans: readonly PlanConnection[]; texts: Record<string, string | undefined>; onOpen: (path: string) => void }) {
  return (
    <div data-testid="wsp-build-plan">
      {plans.length === 0 && <div className="empty" data-testid="wsp-build-no-plan">Nothing is compiled yet. <code>dbdatabuild project compile</code> writes the plan under <code>rendered/</code> and it is committed with the models; a package is made from that.</div>}
      {plans.map((c) => (
        <div key={c.connection} data-testid="wsp-build-connection" data-connection={c.connection}>
          <div className="hint mono" style={{ margin: '8px 0 2px' }}>rendered/{c.connection}</div>
          {c.loads && <div className="grid-row"><button type="button" className="btn-link mono" data-testid="wsp-build-loads" onClick={() => onOpen(c.loads!)}>loads</button><span className="hint">every load of the connection and what each waits for</span></div>}
          {c.plans.map((p) => {
            const text = texts[p.path]
            return (
              <div key={p.path} className="grid-row" style={{ gap: 10 }} data-testid="wsp-build-refresh-plan" data-name={p.name}>
                <button type="button" className="btn-link mono" data-testid="wsp-build-open-plan" onClick={() => onOpen(p.path)}>{p.name}</button>
                <span className="hint">{text === undefined ? '...' : planWord(planSummary(text))}</span>
              </div>
            )
          })}
        </div>
      ))}
      <div className="hint" style={{ marginTop: 8 }}>The plan is written by the tool and committed, so it is read here and not edited: change a model and compile again.</div>
    </div>
  )
}

/** The Models tab of a Build project's workspace: the models, the check of them (its findings open their files) and the compiled plan, over the draft's own files. */
export function BuildPlanPanel({ project, id, paths, onOpen }: { project: string; id: string; paths: readonly string[]; onOpen: (path: string) => void }) {
  const check = useMutation({ mutationFn: () => workspaceApi.check(project, id) })
  const plans = planFilesOf(paths)
  const files = plans.flatMap((c) => c.plans.map((p) => p.path))
  const texts = useQuery({
    queryKey: ['workspace', 'plan-texts', project, id, files],
    queryFn: async () => Object.fromEntries(await Promise.all(files.map(async (f) => [f, await workspaceApi.readFile(project, id, f).catch(() => '')] as const))),
    enabled: files.length > 0,
  })
  return (
    <div data-testid="wsp-build">
      <div className="row" style={{ gap: 8, alignItems: 'center', marginBottom: 8 }}>
        <button type="button" className="btn btn-primary" disabled={check.isPending} data-testid="wsp-build-check" onClick={() => check.mutate()}>{check.isPending ? 'Checking...' : 'Check the models'}</button>
        <span className="hint">Saved files only; nothing is written.</span>
      </div>
      <ErrorBanner error={check.error ?? texts.error} />
      <ModelsView models={modelsOf(paths)} diagnostics={check.data?.diagnostics ?? []} onOpen={onOpen} />
      {check.data && <div style={{ marginTop: 10 }}><CheckResultView result={check.data} onOpen={onOpen} /></div>}
      <div className="hint" style={{ margin: '14px 0 4px' }}>Plan</div>
      <PlanView plans={plans} texts={texts.data ?? {}} onOpen={onOpen} />
    </div>
  )
}
