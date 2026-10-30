import os from 'node:os'
import path from 'node:path'

// Where the through-the-console run (`playwright.config.ts`) leaves what its specs need. Here, not in that config, so a spec's fixtures can read it without loading the config.
export const consoleScratch = path.join(os.tmpdir(), 'dbdatasync-console-e2e')
/** The id the console gave the paired Sync server, written by `console-setup.setup.ts`; its presence is what makes `fixtures.ts` send the specs through the console. */
export const consoleServerIdFile = path.join(consoleScratch, 'server-id.txt')
export const consoleStorageState = path.join(consoleScratch, 'signed-in.json')
