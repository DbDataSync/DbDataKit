import { expect, type Page } from '@playwright/test'

/**
 * Clicks a save button and waits until the PUT it sends has been answered, and asserts the answer is a success.
 *
 * A spec that clicks Save and then reloads, navigates (`page.goto`) or goes on to read what the save should have written
 * abandons the request while it is still in flight. Directly against the server that is usually fast enough to win;
 * through the console's relay it is not, so the reload reads the old configuration. Waiting for the response is the fix,
 * and it belongs in one place, not copied into every spec.
 *
 * The match is on the end of the URL's path, never on the whole address: through the console the same call is
 * `/relay/<id>/api/...` rather than `/api/...`. Pass a string to match a fixed suffix, or a function of the path.
 *
 * Do not use it where the page itself moves on after a successful save (the connection and script editors navigate to
 * their list): wait for the list there. Nor where the save is meant to be refused or held back by a dialog.
 */
export async function clickSave(page: Page, testId: string, pathSuffix: string | ((pathname: string) => boolean)): Promise<void> {
  const answered = page.waitForResponse((r) => {
    if (r.request().method() !== 'PUT') return false
    const pathname = new URL(r.url()).pathname
    return typeof pathSuffix === 'string' ? pathname.endsWith(pathSuffix) : pathSuffix(pathname)
  })
  await page.getByTestId(testId).click()
  const response = await answered
  expect(response.ok(), `saving (${testId}) answered ${response.status()}`).toBe(true)
}

/** The replication's Save settings button (endpoints, pipeline, script bindings, schedule, notes). */
export function saveSettings(page: Page, replicationName: string): Promise<void> {
  return clickSave(page, 'save-settings-button', `/api/replications/${encodeURIComponent(replicationName)}`)
}

/** The table mapping editor's Save mapping button, whichever mapping (or new one) it saves. */
export function saveMapping(page: Page, replicationName: string): Promise<void> {
  const prefix = `/api/replications/${encodeURIComponent(replicationName)}/table-mappings/`
  return clickSave(page, 'save-mapping-button', (pathname) => {
    const at = pathname.indexOf(prefix)
    return at >= 0 && !pathname.slice(at + prefix.length).includes('/')
  })
}
