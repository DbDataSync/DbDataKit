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
  // The run through the console shows the console's frame around the screens: those pictures are not the committed ones and go to a scratch folder.
  const dir = process.env.DBDATASYNC_E2E_CONSOLE === '1'
    ? path.join(os.tmpdir(), 'dbdatasync-console-e2e-screenshots', group)
    : path.resolve(here, '..', '..', 'screenshots', group)
  fs.mkdirSync(dir, { recursive: true })
  return dir
}
