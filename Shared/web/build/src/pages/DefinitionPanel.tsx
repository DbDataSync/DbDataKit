import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'
import { buildApi } from '../api'
import type { CommandAnswer, DefineResult, DefinitionAnswer, DefinitionQuestion } from '../types'

/** What a person has chosen for one question so far: an option and its value, or the tool's proposal taken as it is. */
export interface QuestionChoice {
  choice: string
  value: string
  accepted: boolean
}

export type QuestionChoices = Record<string, QuestionChoice>

/** The answers for these questions, in the form the answers file has; null while one is not answered, or an option that takes a value has none. Nothing is answered for the person. */
export function answersFrom(questions: DefinitionQuestion[], chosen: QuestionChoices): DefinitionAnswer[] | null {
  const answers: DefinitionAnswer[] = []
  for (const q of questions) {
    const c = chosen[q.id]
    if (!c) return null
    if (c.accepted) { answers.push({ id: q.id, accept: 'inferred' }); continue }
    const option = q.options.find((o) => o.key === c.choice)
    if (!option) return null
    if (option.takes_value) {
      if (c.value.trim() === '') return null
      answers.push({ id: q.id, choice: option.key, value: c.value.trim() })
    } else answers.push({ id: q.id, choice: option.key })
  }
  return answers
}

/** The sentence for a definition update that neither wrote nor asked: the first problem the tool named (the unanswered-question notices are the questions themselves), else what it printed. */
export function definitionFailure(answer: CommandAnswer<DefineResult>): string {
  const document = answer.document
  const problem = document?.diagnostics?.find((d) => d.severity === 'error' && d.code !== 'DDB-414')
  if (problem) return problem.found.split('\n')[0]
  return document?.errors?.[0] ?? (answer.text ? answer.text.split('\n')[0] : null) ?? 'The definition could not be brought in line with the query.'
}

export interface QuestionsViewProps {
  questions: DefinitionQuestion[]
  chosen: QuestionChoices
  busy: boolean
  onChoose: (id: string, choice: string) => void
  onValue: (id: string, value: string) => void
  onAccept: (id: string) => void
  onSubmit: () => void
}

/** The questions of `project model update` as a form whose answers are the answers file: each with its words, its options and, when the tool has one, its proposal (taken only by a click). */
export function QuestionsView(p: QuestionsViewProps) {
  const answers = answersFrom(p.questions, p.chosen)
  return (
    <div data-testid="build-definition-questions">
      <div className="hint" style={{ marginBottom: 8 }}>The definition needs your answers before it can be written. Nothing is answered for you.</div>
      {p.questions.map((q) => {
        const c = p.chosen[q.id]
        return (
          <fieldset key={q.id} className="card" style={{ marginBottom: 10 }} data-testid={`build-question-${q.id}`}>
            <legend><strong>{q.prompt}</strong></legend>
            {q.context.map((line, i) => <div key={i} className="hint">{line}</div>)}
            <div role="radiogroup" aria-label={q.prompt} style={{ display: 'grid', gap: 4, marginTop: 6 }}>
              {q.options.map((o) => (
                <label key={o.key} className="row" style={{ gap: 8, alignItems: 'baseline' }}>
                  <input type="radio" name={q.id} checked={c !== undefined && !c.accepted && c.choice === o.key} onChange={() => p.onChoose(q.id, o.key)} data-testid={`build-question-option-${o.key}`} />
                  <span>{o.description}{o.consequence ? <span className="hint"> ({o.consequence})</span> : null}</span>
                  {o.takes_value && c !== undefined && !c.accepted && c.choice === o.key && (
                    <input value={c.value} onChange={(e) => p.onValue(q.id, e.target.value)} placeholder={o.value_hint ?? 'value'} aria-label={o.value_hint ?? 'value'} data-testid="build-question-value" />
                  )}
                </label>
              ))}
            </div>
            {q.proposal && (
              <div className="row" style={{ gap: 8, marginTop: 6 }}>
                <button type="button" className={c?.accepted ? 'btn btn-primary btn-sm' : 'btn btn-sm'} onClick={() => p.onAccept(q.id)} data-testid="build-question-accept" aria-pressed={c?.accepted === true}>
                  Use the proposal: {q.proposal.option}{q.proposal.value ? ` ${q.proposal.value}` : ''}
                </button>
                <span className="hint">{q.proposal.certainty} certainty{q.proposal.evidence?.[0] ? `: ${q.proposal.evidence[0]}` : ''}</span>
              </div>
            )}
          </fieldset>
        )
      })}
      <button type="button" className="btn btn-primary" disabled={answers === null || p.busy} onClick={p.onSubmit} data-testid="build-definition-answer">{p.busy ? 'Writing…' : 'Write the definition'}</button>
    </div>
  )
}

export interface DefinitionResultViewProps {
  failure: string | null
  written: string[] | null
  inSync: boolean
}

export function DefinitionResultView({ failure, written, inSync }: DefinitionResultViewProps) {
  if (failure) return <div className="banner error" role="alert" data-testid="build-definition-failed"><span className="mark">!</span><span>{failure}</span></div>
  if (written && written.length > 0) return <div className="hint" data-testid="build-definition-written">Wrote {written.join(', ')}. The definition is in line with the query.</div>
  if (inSync) return <div className="hint" data-testid="build-definition-in-sync">The definition already matches the query.</div>
  return null
}

/**
 * A model's definition and its query (plan 13, W07): bring the definition's columns in line with the query as it was last saved. When the tool has questions (a new column's type and whether it can be NULL, a model's grain) they are
 * shown as a form, the answers are written to an answers file in the draft (a folder git ignores) and the update is run again with it. No connection is read.
 */
export function DefinitionPanel({ project, workspace, path, dirty, onWritten }: { project: string; workspace: string; path: string; dirty: boolean; onWritten: () => void }) {
  const client = useQueryClient()
  const [questions, setQuestions] = useState<DefinitionQuestion[]>([])
  const [chosen, setChosen] = useState<QuestionChoices>({})
  const [failure, setFailure] = useState<string | null>(null)
  const [written, setWritten] = useState<string[] | null>(null)
  const [inSync, setInSync] = useState(false)

  const settle = async (answer: CommandAnswer<DefineResult>) => {
    const model = answer.document?.data?.models?.[0]
    const open = model?.open_questions ?? []
    setFailure(null); setWritten(null); setInSync(false)
    if (open.length > 0) {
      setQuestions(open)
      // a new round keeps what was already chosen for a question that is asked again
      setChosen((before) => Object.fromEntries(open.filter((q) => before[q.id]).map((q) => [q.id, before[q.id]])))
      return
    }
    setQuestions([]); setChosen({})
    if (answer.exit === 0 && answer.document?.ok) {
      const files = answer.document.data?.written ?? []
      if (files.length > 0) { setWritten(files); await client.invalidateQueries({ queryKey: ['build', project, workspace] }); onWritten() } else setInSync(true)
      return
    }
    setFailure(definitionFailure(answer))
  }

  const update = useMutation({ mutationFn: () => buildApi.updateDefinition(project, workspace, path), onSuccess: settle })
  const answer = useMutation({
    mutationFn: async () => {
      const given = answersFrom(questions, chosen)
      if (!given) throw new Error('Answer every question first.')
      const file = await buildApi.writeAnswers(project, workspace, given)
      if (!file) throw new Error('The answers could not be saved in the workspace.')
      return buildApi.updateDefinition(project, workspace, path, file)
    },
    onSuccess: settle,
  })
  const busy = update.isPending || answer.isPending

  return (
    <div className="card" style={{ marginTop: 14 }} data-testid="build-definition">
      <div className="row" style={{ gap: 12, marginBottom: 8, flexWrap: 'wrap' }}>
        <strong>Definition</strong>
        <button type="button" className="btn" disabled={busy || dirty} onClick={() => update.mutate()} data-testid="build-definition-update">{update.isPending ? 'Checking…' : 'Update the definition from the query'}</button>
        <span className="hint">{dirty ? 'Save the query first: the definition is brought in line with the saved file.' : 'Brings the columns of the definition in line with the query. Nothing is connected to.'}</span>
      </div>
      <DefinitionResultView failure={failure} written={written} inSync={inSync} />
      {questions.length > 0 && (
        <QuestionsView
          questions={questions}
          chosen={chosen}
          busy={answer.isPending}
          onChoose={(id, choice) => setChosen((c) => ({ ...c, [id]: { choice, value: c[id]?.choice === choice ? c[id].value : '', accepted: false } }))}
          onValue={(id, value) => setChosen((c) => ({ ...c, [id]: { ...c[id], value } }))}
          onAccept={(id) => setChosen((c) => ({ ...c, [id]: { choice: '', value: '', accepted: true } }))}
          onSubmit={() => answer.mutate()}
        />
      )}
      <ErrorBanner error={update.error ?? answer.error} />
    </div>
  )
}
