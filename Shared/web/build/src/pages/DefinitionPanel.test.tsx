import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { answersYaml } from '../api'
import type { DefinitionQuestion } from '../types'
import { DefinitionResultView, QuestionsView, answersFrom, definitionFailure, type QuestionChoices, type QuestionsViewProps } from './DefinitionPanel'

const nullable: DefinitionQuestion = {
  id: 'Q-define-marts.fct_returns-columns.label.nullable',
  prompt: 'Can column `label` be NULL?',
  context: ['non-null by lineage (declared NOT NULL upstream, no outer join).'],
  options: [{ key: 'nullable', description: 'It can be NULL', takes_value: false }, { key: 'not_nullable', description: 'It is never NULL', consequence: 'the column is declared NOT NULL', takes_value: false }],
  proposal: { option: 'not_nullable', certainty: 'high', evidence: ['non-null by lineage'] },
}
const type: DefinitionQuestion = {
  id: 'Q-define-marts.fct_returns-columns.label.type',
  prompt: 'What is the type of column `label`?',
  context: ['DuckDB resolves the query column to VARCHAR.'],
  options: [{ key: 'use_type', description: 'Use this SQL type', takes_value: true, value_hint: 'SQL type, for example VARCHAR(50)' }, { key: 'skip_model', description: 'Skip this model for now', takes_value: false }],
  proposal: { option: 'use_type', value: 'VARCHAR', certainty: 'high', evidence: ['DuckDB reports VARCHAR'] },
}
const questions = [nullable, type]

function props(over: Partial<QuestionsViewProps> = {}): QuestionsViewProps {
  return { questions, chosen: {}, busy: false, onChoose: () => {}, onValue: () => {}, onAccept: () => {}, onSubmit: () => {}, ...over }
}

function find(node: ReactNode, testId: string): ReactElement<Record<string, unknown>> | undefined {
  if (Array.isArray(node)) { for (const child of node) { const found = find(child, testId); if (found) return found } return undefined }
  if (!isValidElement(node)) return undefined
  const element = node as ReactElement<Record<string, unknown>>
  if (element.props['data-testid'] === testId) return element
  if (typeof element.type === 'function') return find((element.type as (p: unknown) => ReactNode)(element.props), testId)
  return find(element.props.children as ReactNode, testId)
}

describe('the definition questions as a form', () => {
  it('shows each question with its words, its options with their consequences, and its proposal', () => {
    const html = renderToStaticMarkup(QuestionsView(props()))
    expect(html).toContain('Can column `label` be NULL?'.replace(/`/g, '`'))
    expect(html).toContain('non-null by lineage')
    expect(html).toContain('the column is declared NOT NULL')
    expect(html).toContain('Use the proposal: not_nullable')
    expect(html).toContain('Use the proposal: use_type VARCHAR')
    expect(html).toContain('high certainty')
    expect(html).not.toContain('checked')
  })

  it('answers nothing for the person: nothing can be written until every question has an answer', () => {
    const disabled = (chosen: QuestionChoices) => find(QuestionsView(props({ chosen })), 'build-definition-answer')?.props.disabled
    expect(disabled({})).toBe(true)
    expect(disabled({ [nullable.id]: { choice: 'not_nullable', value: '', accepted: false } })).toBe(true)
    expect(disabled({ [nullable.id]: { choice: 'not_nullable', value: '', accepted: false }, [type.id]: { choice: 'use_type', value: '  ', accepted: false } })).toBe(true)   // a value is needed
    expect(disabled({ [nullable.id]: { choice: 'not_nullable', value: '', accepted: false }, [type.id]: { choice: 'use_type', value: 'VARCHAR(40)', accepted: false } })).toBe(false)
    expect(disabled({ [nullable.id]: { choice: '', value: '', accepted: true }, [type.id]: { choice: '', value: '', accepted: true } })).toBe(false)
  })

  it('writes the answers as the answers file has them: a choice with its value, or the proposal accepted', () => {
    const answers = answersFrom(questions, { [nullable.id]: { choice: 'nullable', value: '', accepted: false }, [type.id]: { choice: 'use_type', value: ' VARCHAR(40) ', accepted: false } })
    expect(answers).toEqual([{ id: nullable.id, choice: 'nullable' }, { id: type.id, choice: 'use_type', value: 'VARCHAR(40)' }])
    expect(answersYaml(answers!)).toBe(`answers:\n  - id: ${nullable.id}\n    choice: nullable\n  - id: ${type.id}\n    choice: use_type\n    value: "VARCHAR(40)"\n`)
    const accepted = answersFrom(questions, { [nullable.id]: { choice: '', value: '', accepted: true }, [type.id]: { choice: 'skip_model', value: '', accepted: false } })
    expect(answersYaml(accepted!)).toBe(`answers:\n  - id: ${nullable.id}\n    accept: inferred\n  - id: ${type.id}\n    choice: skip_model\n`)
    expect(answersFrom(questions, {})).toBeNull()
    expect(answersFrom(questions, { [nullable.id]: { choice: 'no_such_option', value: '', accepted: false }, [type.id]: { choice: 'skip_model', value: '', accepted: false } })).toBeNull()
  })

  it('asks for the value only under the option that takes one, and passes what is typed up', () => {
    const calls: string[] = []
    const chosen: QuestionChoices = { [type.id]: { choice: 'use_type', value: '', accepted: false } }
    const view = QuestionsView(props({ chosen, onValue: (id, v) => calls.push(`${id} ${v}`) }))
    expect(renderToStaticMarkup(view)).toContain('SQL type, for example VARCHAR(50)')
    expect(renderToStaticMarkup(QuestionsView(props()))).not.toContain('build-question-value')
    ;(find(view, 'build-question-value')!.props.onChange as (e: unknown) => void)({ target: { value: 'TEXT' } })
    expect(calls).toEqual([`${type.id} TEXT`])
  })

  it('takes the proposal only on a click', () => {
    const calls: string[] = []
    const view = QuestionsView(props({ questions: [nullable], onAccept: (id) => calls.push(id) }))
    ;(find(view, 'build-question-accept')!.props.onClick as () => void)()
    expect(calls).toEqual([nullable.id])
  })
})

describe('what an update of the definition says when it asks nothing', () => {
  it('names the first problem, not the notices that stand for the questions', () => {
    const notice = { code: 'DDB-414', severity: 'error' as const, title: 'Question is unanswered', location: { file: '<answers>', line: 0, column: 0 }, found: 'Q-define-x\n  Can it be NULL?', supported: '', fix: '' }
    const real = { code: 'DDB-420', severity: 'error' as const, title: 'Out of sync', location: { file: 'models/a.yml', line: 3, column: 1 }, found: '`models/a.yml` does not match its query.\nMore.', supported: '', fix: '' }
    expect(definitionFailure({ exit: 1, text: null, document: { ok: false, data: { mode: 'write', models: [] }, diagnostics: [notice, real] } })).toBe('`models/a.yml` does not match its query.')
    expect(definitionFailure({ exit: 2, text: 'No such model.\n  at Trace()', document: null })).toBe('No such model.')
    expect(definitionFailure({ exit: 1, text: null, document: null })).toContain('could not be brought in line')
  })

  it('reports what was written, or that nothing needed changing, or the failure', () => {
    expect(renderToStaticMarkup(DefinitionResultView({ failure: null, written: ['models/a.yml'], inSync: false }))).toContain('Wrote models/a.yml')
    expect(renderToStaticMarkup(DefinitionResultView({ failure: null, written: null, inSync: true }))).toContain('already matches')
    expect(renderToStaticMarkup(DefinitionResultView({ failure: 'Broken.', written: null, inSync: false }))).toContain('build-definition-failed')
    expect(renderToStaticMarkup(DefinitionResultView({ failure: null, written: null, inSync: false }))).toBe('')
  })
})
