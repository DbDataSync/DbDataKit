import fs from 'node:fs'
import path from 'node:path'
import os from 'node:os'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))

/**
 * `<repo-root>/screenshots/<group>/`, created on first call.
 *
 * The committed UI screenshots live at the top level of the repo, not under this test project, so a
 * reader browsing for "what does the console look like" finds them without digging into a test dir —
 * one folder per spec, so a numeric prefix reused across specs (`62-`, `90-`) never collides.
 */
export function screenshotDir(group: string): string {
  // The screens are shown inside the console's frame, so a run does not overwrite the committed pictures (which the docs show) with them: they go to a scratch folder, unless DBDATASYNC_E2E_SCREENSHOTS=repo says
  // to replace the committed ones on purpose.
  const dir = process.env.DBDATASYNC_E2E_SCREENSHOTS === 'repo'
    ? path.resolve(here, '..', '..', 'screenshots', group)
    : path.join(os.tmpdir(), 'dbdatasync-console-e2e-screenshots', group)
  fs.mkdirSync(dir, { recursive: true })
  return dir
}
