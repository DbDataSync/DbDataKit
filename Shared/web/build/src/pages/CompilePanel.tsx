import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'
import { buildApi } from '../api'
import type { CommandAnswer, CompileSummary } from '../types'

/** What a compile that wrote said, in a sentence: how many files it wrote or removed, or that the rendered files were right already. */
export function compiledSentence(summary: CompileSummary): string {
  const wrote = summary.wrote?.length ?? 0
  const removed = summary.removed?.length ?? 0
  if (wrote === 0 && removed === 0) return 'The rendered files were up to date; nothing was written.'
  return `Wrote ${wrote} rendered ${wrote === 1 ? 'file' : 'files'}${removed > 0 ? ` and removed ${removed}` : ''}. They are part of the draft now: commit them with the rest.`
}

/** Why the compile wrote nothing, in the tool's own words (a model that does not compile is the first reason). */
export function compileFailure(answer: CommandAnswer<CompileSummary>): string | null {
  if (answer.document?.ok) return null
  const problem = answer.document?.diagnostics?.find((d) => d.severity === 'error')
  return problem ? `${problem.code}: ${problem.found}` : answer.document?.errors?.[0] ?? (answer.text ? answer.text.split('\n')[0] : null) ?? 'The project could not be compiled.'
}

export interface CompileViewProps {
  /** Files the last check said a compile would write (the rendered files a deploy plans from); null before the first check has answered. */
  outOfDate: number | null
  busy: boolean
  done: string | null
  failure: string | null
  onCompile: () => void
}

export function CompileView({ outOfDate, busy, done, failure, onCompile }: CompileViewProps) {
  return (
    <div className="row" style={{ gap: 12, marginBottom: 14, flexWrap: 'wrap' }} data-testid="build-compile">
      <button type="button" className={outOfDate !== null && outOfDate > 0 ? 'btn btn-primary' : 'btn'} disabled={busy} onClick={onCompile} data-testid="build-compile-button">{busy ? 'Compiling…' : 'Write the compiled files'}</button>
      <span className="hint" data-testid="build-compile-hint">
        {outOfDate === null ? '' : outOfDate > 0 ? `${outOfDate} rendered ${outOfDate === 1 ? 'file is' : 'files are'} out of date. ` : 'The rendered files match the models. '}A deploy plans from the rendered files, so write them before the draft is committed.
      </span>
      {done && <span className="hint" data-testid="build-compile-done">{done}</span>}
      {failure && <div className="empty" data-testid="build-compile-failed">{failure}</div>}
    </div>
  )
}

/**
 * Compiling for real, in a draft (plan 13a, W10): `project compile` writes the rendered files a deploy plans from (a deploy refuses a project whose committed `rendered/` differs from a fresh render, DDB-424).
 * Saving a file only checks; this is the one place the console writes them. The author role, like every command that writes the draft's files.
 */
export function CompilePanel({ project, workspace, outOfDate, onWritten }: { project: string; workspace: string; outOfDate: number | null; onWritten: () => void }) {
  const client = useQueryClient()
  const compile = useMutation({
    mutationFn: () => buildApi.compileDraft(project, workspace),
    onSuccess: async (answer) => {
      if (!answer.document?.ok) return
      await client.invalidateQueries({ queryKey: ['build', project, workspace, 'files'] })
      await client.invalidateQueries({ queryKey: ['build', project, workspace, 'git'] })
      onWritten()
    },
  })
  const failure = compile.data ? compileFailure(compile.data) : null
  const done = compile.data?.document?.ok ? compiledSentence(compile.data.document.data) : null
  return (
    <>
      <CompileView outOfDate={outOfDate} busy={compile.isPending} done={done} failure={failure} onCompile={() => compile.mutate()} />
      <ErrorBanner error={compile.error} />
    </>
  )
}
