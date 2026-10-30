import { test as base, type APIRequestContext, type Page } from '@playwright/test'
import fs from 'node:fs'
import { consoleServerIdFile } from './console-paths'

export * from '@playwright/test'

/**
 * The suite runs two ways (plan 13a, U08): directly against a Sync server (`playwright.config.ts`), and through the console against a paired one (`playwright.console.config.ts`).
 * The specs are written in the server's own addresses (`/replications/...`, `/api/...`). Through the console the same screens live under `/servers/<id>/...` and their API calls go
 * to the console's relay at `/relay/<id>/api/...`, so the page a spec gets is one that says the same addresses and has them translated: `goto`, and the `page.request` calls. Everything the
 * browser itself does (links, redirects, reloads, the app's own requests) is not translated: those are exactly what the run is there to test.
 * Only when `DBDATASYNC_E2E_CONSOLE=1` (set by that config) and the setup has named the server; otherwise (the direct run) the page is the plain one.
 */
function serverId(): string | undefined {
  if (process.env.DBDATASYNC_E2E_CONSOLE !== '1') return undefined
  try {
    return fs.readFileSync(consoleServerIdFile, 'utf8').trim() || undefined
  } catch {
    return undefined
  }
}

const requestMethods = new Set(['get', 'post', 'put', 'patch', 'delete', 'head', 'fetch'])

function relayed(url: string, id: string): string {
  return url.startsWith('/api/') || url === '/api' ? `/relay/${id}${url}` : url
}

function throughConsole(page: Page, id: string): Page {
  const wrapRequest = (request: APIRequestContext) => new Proxy(request, {
    get(target, prop) {
      const value = Reflect.get(target, prop, target)
      if (typeof value !== 'function') return value
      if (typeof prop === 'string' && requestMethods.has(prop)) {
        return (url: unknown, ...rest: unknown[]) => (value as (...a: unknown[]) => unknown).call(target, typeof url === 'string' ? relayed(url, id) : url, ...rest)
      }
      return value.bind(target)
    },
  })
  return new Proxy(page, {
    get(target, prop) {
      const value = Reflect.get(target, prop, target)
      if (prop === 'goto') {
        return (url: string, options?: Parameters<Page['goto']>[1]) =>
          target.goto(url.startsWith('/') && !url.startsWith('//') ? `/servers/${id}${url}` : url, options)
      }
      if (prop === 'request') return wrapRequest(value as APIRequestContext)
      return typeof value === 'function' ? value.bind(target) : value
    },
  })
}

export const test = base.extend<object>({
  page: async ({ page }, use) => {
    const id = serverId()
    await use(id ? throughConsole(page, id) : page)
  },
})
