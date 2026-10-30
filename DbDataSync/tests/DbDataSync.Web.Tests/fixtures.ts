import { test as base, type APIRequestContext, type Page } from '@playwright/test'
import fs from 'node:fs'
import { consoleServerIdFile } from './console-paths'

export * from '@playwright/test'

/**
 * The specs are written in the server's own addresses (`/replications/...`, `/api/...`). The suite runs through the console (`playwright.config.ts`), where the same screens live under `/servers/<id>/...` and
 * their API calls go to the console's relay at `/relay/<id>/api/...`, so the page a spec gets is one that says the same addresses and has them translated: `goto`, and the `page.request` calls. Everything the
 * browser itself does (links, redirects, reloads, the app's own requests) is not translated: those are exactly what the run is there to test.
 */
function serverId(): string {
  const id = fs.existsSync(consoleServerIdFile) ? fs.readFileSync(consoleServerIdFile, 'utf8').trim() : ''
  if (!id) throw new Error(`No paired server is named in ${consoleServerIdFile}: the console-setup project signs in and pairs the server first (run the suite with playwright.config.ts, not a single file without its dependency).`)
  return id
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
    await use(throughConsole(page, serverId()))
  },
})
