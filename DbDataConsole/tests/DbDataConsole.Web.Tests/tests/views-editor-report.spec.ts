import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { editorReportStory } from './views-editor-report'

// Plan 32, DBR27 (M2 Edit, reports): the report editor builds a report: page setup, header and footer with tokens, sections, a row of spans, a break, strict and limits; the paged preview follows the page size. The story is in views-editor-report.ts.
// Same shape as `views-editor`: a new administrator, the console only, no port (it sorts after `identity-home` and `first-run`).
test('the editor builds a report: page, running text, sections, blocks, options, preview, save', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Report Tester', `views-editor-report-${Date.now()}@example.com`)
  await editorReportStory(page)
})
