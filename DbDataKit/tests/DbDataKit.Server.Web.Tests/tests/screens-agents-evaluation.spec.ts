import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { mockAgentHost } from './agents-screens'
import { evaluationStory, mockEvaluation } from './agents-evaluation-screens'

// Plan 27a AG23 and AG24: the evaluation panel of an agent with the agent host and the evaluation host mocked in the page (the host side is DbDataKit.Agents.Tests' EvaluationTests and DbDataKit.Server.Tests' ConsoleEvaluationTests, on the
// real code with a scripted model). The name sorts after first-run and identity-home: it invites a console administrator.
test('a run of an agent\'s set is started, each case shows what the suite measured and what the judge judged, a minimum is recorded and a promotion below it is refused naming the score', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Evaluation Tester', `evaluation-${Date.now()}@example.com`)
  await mockAgentHost(page)
  const mock = await mockEvaluation(page)
  await evaluationStory(page, mock)
})
