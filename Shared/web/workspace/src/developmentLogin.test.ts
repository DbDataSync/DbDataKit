import { afterEach, describe, expect, it, vi } from 'vitest'
import { apiUrl, setApiBase } from '@dbdatakit/shell'
import { developmentLoginApi, developmentLoginLine, developmentLoginWarning, developmentLoginWarnings, type DevelopmentLoginAnswer } from './developmentLogin'

afterEach(() => {
  setApiBase('')
  vi.unstubAllGlobals()
})

describe('a development login, as a Workspace screen sends it', () => {
  it('goes to the console\'s own route even when the screens talk to a Workspace server through the relay', () => {
    setApiBase('/relay/ws-1')
    expect(apiUrl('/api/workspace-dev-logins/ws-1/crm-to-dw')).toBe('/api/workspace-dev-logins/ws-1/crm-to-dw')
    expect(apiUrl('/api/v1/projects/crm-to-dw')).toBe('/relay/ws-1/api/v1/projects/crm-to-dw')
  })

  it('sends the login once, to the console, and never to a Workspace server\'s relay', async () => {
    setApiBase('/relay/ws-1')
    const answer: DevelopmentLoginAnswer = {
      package: 'crm-to-dw', connection: 'crm', slot: 'crm', channel: 'dev', production: false, runner: 'r-1', runner_name: 'runners-eu', user: 'ana_dev',
      secrets: { password: 'dbdatakit:runner:dev-login:crm-to-dw.dev.crm:password' },
    }
    const calls: { url: string; body: string }[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init: RequestInit) => {
      calls.push({ url, body: String(init.body) })
      return new Response(JSON.stringify(answer), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }))

    const got = await developmentLoginApi.setUp('ws-1', 'crm-to-dw', { runner: 'r-1', channel: 'dev', connection: 'crm', user: 'ana_dev', password: 'pw-7731' })

    expect(calls).toHaveLength(1)
    expect(calls[0]!.url).toBe('/api/workspace-dev-logins/ws-1/crm-to-dw')
    expect(JSON.parse(calls[0]!.body)).toEqual({ runner: 'r-1', channel: 'dev', connection: 'crm', user: 'ana_dev', password: 'pw-7731' })
    expect(calls.some((c) => c.url.startsWith('/relay/'))).toBe(false)
    expect(developmentLoginLine(got)).toBe('runners-eu holds the login of crm for dev.')
    expect(JSON.stringify(got)).not.toContain('pw-7731')
    expect(got.user).toBe('ana_dev')
    expect(Object.keys(got.secrets)).toEqual(['password'])
  })

  it('warns where the login goes before it is typed', () => {
    expect(developmentLoginWarning('crm', 'dev', 'runners-eu')).toBe(
      'The login you type for crm goes to runners-eu, which keeps it for the dev channel only. It is not saved in the project, its history or the Workspace.')
  })

  it('says the channel reads the connection through the copy once the set-up binds it, and picks the warnings out of the steps', () => {
    const bound: DevelopmentLoginAnswer = {
      package: 'crm-to-dw', connection: 'crm', slot: 'crm', channel: 'dev', production: false, runner: 'r-1', runner_name: 'runners-eu', user: 'ana_dev',
      secrets: { password: 'dbdatakit:runner:dev-login:crm-to-dw.dev.crm:password' }, bound: true, copy: 'crm-to-dw-crm-dev',
      steps: ['Stored the login.', "runners-eu keeps the connection 'crm-to-dw-crm-dev'.", 'Warning: the draft names no address.', "Bound the slot 'crm'."],
    }
    expect(developmentLoginLine(bound)).toBe('runners-eu holds the login of crm for dev, and dev reads crm through crm-to-dw-crm-dev.')
    expect(developmentLoginWarnings(bound)).toEqual(['Warning: the draft names no address.'])
    expect(developmentLoginWarnings({ ...bound, steps: undefined })).toEqual([])
  })
})
