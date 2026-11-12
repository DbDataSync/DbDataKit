import { expect, type Page, type Route } from '@playwright/test'

// Plan 27a AG23 and AG24: the evaluation panel of an agent against a fake evaluation host, mocked in the page. The host side (the cases read strictly, the suite's checks, the judge, the rows compared with a verified query, the record of a
// run, the minimum and the refusal) is `DbDataKit.Agents.Tests` EvaluationTests and `DbDataKit.Server.Tests` ConsoleEvaluationTests on the real code with a scripted model; what this story shows is the screen: start a run, see the case
// results with the judgement shown as a judgement, set a minimum, see a promotion refused.

const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })

export interface EvaluationMock { runs: number; minimum: number | null }

export async function mockEvaluation(page: Page): Promise<EvaluationMock> {
  const mock: EvaluationMock = { runs: 0, minimum: null }
  const refusal = () => (mock.minimum !== null && mock.runs > 0 && 0.42 < mock.minimum
    ? `helper abc123 cannot be promoted to main: its last evaluation scored 0.42 (1 of 2 cases passed), below the minimum of ${mock.minimum.toFixed(2)} set for main.` : null)
  const state = () => ({
    agent: 'helper', version: 'abc123', channel: 'main', judgeBound: true, running: null, diagnostics: [],
    cases: [
      { name: 'widget', file: 'agents/helper/tests/evaluation/widget.yml', task: 'What is the widget?', checks: ['tools-called: thing_show', 'answer-contains: widget', 'rubric: The answer says what the widget is.'], hasRubric: true, passAt: 0.8 },
      { name: 'gadget', file: 'agents/helper/tests/evaluation/gadget.yml', task: 'What is the gadget?', checks: ['answer-contains: gadget'], hasRubric: false, passAt: 1 },
    ],
    runs: mock.runs === 0 ? [] : [{ id: 'run1', agent: 'helper', agentVersion: 'abc123', channel: 'main', model: 'scripted', knowledgeVersions: ['policy/refunds.md@v3'], trigger: 'manual', score: 0.42, passed: 1, total: 2, cost: 0.0016, startedAt: '2026-11-12T09:00:00Z', finishedAt: '2026-11-12T09:00:30Z' }],
    minimum: mock.minimum === null ? null : { minimum: mock.minimum, setBy: 'Evaluation Tester', setAt: '2026-11-12T09:10:00Z' },
    promotion: { version: 'abc123', refusal: refusal() },
  })
  await page.route('**/api/agents/helper/evaluation', (route) => json(route, state()))
  await page.route('**/api/agents/helper/evaluation/run', (route) => { mock.runs++; return json(route, { started: true }, 202) })
  await page.route('**/api/agents/helper/evaluation/minimum', (route) => {
    mock.minimum = (route.request().postDataJSON() as { minimum: number | null }).minimum
    return json(route, { minimum: mock.minimum, promotion: state().promotion })
  })
  await page.route('**/api/agents/helper/evaluation/runs/run1', (route) => json(route, {
    run: state().runs[0],
    cases: [
      { name: 'gadget', task: 'What is the gadget?', score: 0, passed: false, answer: 'I do not know.', inputTokens: 300, outputTokens: 20, cost: 0.0003, problem: null,
        checks: [{ kind: 'answer-contains', detail: 'answer-contains: gadget', method: 'suite', score: 0, message: 'The answer does not say it.', judgement: null }] },
      { name: 'widget', task: 'What is the widget?', score: 0.8333, passed: true, answer: 'It is a widget.', inputTokens: 1100, outputTokens: 100, cost: 0.0013, problem: null,
        checks: [
          { kind: 'tools-called', detail: 'tools-called: thing_show', method: 'suite', score: 1, message: 'All were called.', judgement: null },
          { kind: 'answer-contains', detail: 'answer-contains: widget', method: 'suite', score: 1, message: 'The answer says it.', judgement: null },
          { kind: 'rubric', detail: 'rubric: The answer says what the widget is.', method: 'judge', score: 0.5, message: 'The judge model scored it 0.5.', judgement: 'It names the widget but not what it is for.' },
        ] },
    ],
  }))
  return mock
}

export async function evaluationStory(page: Page, mock: EvaluationMock) {
  await page.goto('/agents?tab=agents&agent=helper')
  const panel = page.getByTestId('evaluation')
  await expect(panel).toBeVisible()
  await expect(page.getByTestId('evaluation-case')).toHaveCount(2)
  await expect(page.getByTestId('evaluation-no-runs')).toBeVisible()
  await expect(page.getByTestId('evaluation-promotion')).toContainText('No minimum score is set for main')

  // start a run by hand: the record appears with the version, the model, the cases that passed, the score and the cost
  await page.getByTestId('evaluation-run').click()
  await expect(page.getByTestId('evaluation-run-line')).toContainText('Version abc123 on scripted: 1 of 2 cases passed, score 42%, cost $0.0016.')
  expect(mock.runs).toBe(1)

  // per case: what the suite measured and, apart, what the judge model judged, shown as a judgement
  await page.getByTestId('evaluation-run-open').click()
  const widget = page.getByTestId('evaluation-case-result').filter({ hasText: 'widget' }).first()
  await expect(widget.getByTestId('evaluation-case-score')).toHaveText('83%')
  const judged = widget.locator('[data-method="judge"]')
  await expect(judged).toContainText('a judgement, not a measurement')
  await expect(judged).toContainText('It names the widget but not what it is for.')
  await expect(widget.locator('[data-method="suite"]').first()).toContainText('Held')
  await expect(page.getByTestId('evaluation-case-result').filter({ hasText: 'gadget' }).first()).toHaveAttribute('data-passed', 'false')

  // a minimum is a recorded choice; a promotion below it is refused, naming the score
  await page.getByTestId('evaluation-minimum-input').fill('80')
  await page.getByTestId('evaluation-minimum-set').click()
  await expect(page.getByTestId('evaluation-minimum')).toContainText('Minimum 80% for main, set by Evaluation Tester')
  await expect(page.getByTestId('evaluation-promotion')).toHaveAttribute('data-refused', 'true')
  await expect(page.getByTestId('evaluation-promotion')).toContainText('its last evaluation scored 0.42 (1 of 2 cases passed), below the minimum of 0.80 set for main')
  await page.getByTestId('evaluation-minimum-input').fill('40')
  await page.getByTestId('evaluation-minimum-set').click()
  await expect(page.getByTestId('evaluation-promotion')).toHaveAttribute('data-refused', 'false')
}
