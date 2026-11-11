import { expect, type Page, type Route } from '@playwright/test'

// Plan 33, UE15: the attribute mappings of the identity home on the Gateway's Identity home screen, against a Gateway whose relay is mocked in the page (the Gateway side, with the roles, the rules and the audit, is
// DbDataGateway.Tests' IdentityModuleTests; the rules of the home itself are DbDataKit.Identity.Tests' DirectoryAttributeTests). The mock keeps the mappings and every body the screen sent.

const SERVER = 'gw-1'
const json = (route: Route, body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })

interface Mapping { id: string; source: string; group: string; attribute: string; value: string }

export interface AttributesMock { sent: Record<string, unknown[]>; mappings: Mapping[] }

export async function mockIdentityHome(page: Page): Promise<AttributesMock> {
  const mock: AttributesMock = { sent: {}, mappings: [] }
  let next = 1
  await page.route('**/api/instances', (route) => route.request().method() === 'GET'
    ? json(route, [{ id: SERVER, name: 'Identity gateway', url: 'https://gateway.invalid', product: 'dbdatagateway', version: '1', apiVersion: 1, reachable: true, ready: true, problem: null, pairedAt: null }])
    : route.fallback())
  await page.route(`**/relay/${SERVER}/api/v1/identity/**`, async (route) => {
    const request = route.request()
    const method = request.method()
    const tail = new URL(request.url()).pathname.slice(`/relay/${SERVER}/api/v1/identity/`.length)
    const body = method === 'POST' ? request.postDataJSON() as Record<string, string> : undefined
    if (method !== 'GET') (mock.sent[`${method} ${tail}`] ??= []).push(body ?? null)
    const lists: Record<string, string> = { people: 'people', groups: 'groups', assignments: 'assignments', 'group-mappings': 'group_mappings', clients: 'clients', 'service-accounts': 'service_accounts', tokens: 'tokens' }
    if (method === 'GET' && tail in lists) return json(route, { [lists[tail]!]: [] })
    if (tail === 'attribute-mappings' && method === 'GET') return json(route, { attribute_mappings: mock.mappings })
    if (tail === 'attribute-mappings' && method === 'POST') {
      // the rules of the home, in its words
      if (!/^[a-z][a-z0-9_]{0,63}$/.test(body!.attribute!)) return json(route, { error: `An attribute name is lower case letters, digits and underscores, starting with a letter, at most 64 characters; '${body!.attribute}' is not.` }, 400)
      if (body!.group === 'CORP\\Nobody') return json(route, { error: 'Administering the identity home needs the admin role.' }, 403)
      if (!mock.mappings.some((m) => m.source === body!.source && m.group === body!.group && m.attribute === body!.attribute && m.value === body!.value))
        mock.mappings.push({ id: `m${next++}`, source: body!.source!, group: body!.group!, attribute: body!.attribute!, value: body!.value! })
      return json(route, { attribute_mappings: mock.mappings })
    }
    if (tail.startsWith('attribute-mappings/') && method === 'DELETE') {
      const id = tail.slice('attribute-mappings/'.length)
      const at = mock.mappings.findIndex((m) => m.id === id)
      if (at < 0) return json(route, { error: 'There is no such attribute mapping.' }, 404)
      mock.mappings.splice(at, 1)
      return route.fulfill({ status: 204 })
    }
    return json(route, { error: 'There is no such call.' }, 404)
  })
  return mock
}

/**
 * UE15: a person makes a group give its members a value, sees it listed and summarised, is told what is missing and what the home would refuse before anything is sent, sees the home's refusal in its words, and removes a mapping.
 */
export async function attributesStory(page: Page) {
  const outside: string[] = []
  const origin = new URL(page.url()).origin
  page.on('request', (request) => { if (/^https?:/.test(request.url()) && !request.url().startsWith(origin)) outside.push(request.url()) })
  const mock = await mockIdentityHome(page)

  await page.goto(`/servers/${SERVER}/identity-home`)
  await expect(page.getByTestId('identity-admin')).toBeVisible()
  await expect(page.getByTestId('identity-attribute-empty')).toBeVisible()

  // nothing can be sent until the form is complete, and the button says what is missing
  await expect(page.getByTestId('identity-attribute-add')).toBeDisabled()
  await expect(page.getByTestId('identity-attribute-why')).toHaveText('Name the external group.')
  await page.getByTestId('identity-attribute-group').fill('00000000-0000-0000-0000-00000000ea57')
  await expect(page.getByTestId('identity-attribute-why')).toHaveText('Name the attribute.')
  await page.getByTestId('identity-attribute-name').fill('Region')
  await expect(page.getByTestId('identity-attribute-problem')).toContainText('lower case letters, digits and underscores')
  await expect(page.getByTestId('identity-attribute-add')).toBeDisabled()
  await page.getByTestId('identity-attribute-name').fill('region')
  await expect(page.getByTestId('identity-attribute-why')).toHaveText('Give the value the group gives.')
  await page.getByTestId('identity-attribute-value').fill(' east ')
  await expect(page.getByTestId('identity-attribute-add')).toBeEnabled()
  await page.getByTestId('identity-attribute-add').click()

  // the row is listed, the fields are cleared, and what was sent is trimmed
  await expect(page.getByTestId('identity-attribute-region-east')).toContainText('00000000-0000-0000-0000-00000000ea57')
  await expect(page.getByTestId('identity-attribute-summary-region')).toHaveText('region: east')
  await expect(page.getByTestId('identity-attribute-group')).toHaveValue('')
  expect(mock.sent['POST attribute-mappings']!.at(-1)).toEqual({ source: 'entra', group: '00000000-0000-0000-0000-00000000ea57', attribute: 'region', value: 'east' })

  // a second group (of the other directory) gives a second value of the same attribute; both are summarised
  await page.getByTestId('identity-attribute-source').selectOption('windows')
  await page.getByTestId('identity-attribute-group').fill('CORP\\West')
  await page.getByTestId('identity-attribute-name').fill('region')
  await page.getByTestId('identity-attribute-value').fill('west')
  await page.getByTestId('identity-attribute-add').click()
  await expect(page.getByTestId('identity-attribute-region-west')).toContainText('CORP\\West')
  await expect(page.getByTestId('identity-attribute-summary-region')).toHaveText('region: east, west')

  // the same mapping again is not offered (the group already gives it)
  await page.getByTestId('identity-attribute-group').fill('CORP\\West')
  await page.getByTestId('identity-attribute-name').fill('region')
  await page.getByTestId('identity-attribute-value').fill('west')
  await expect(page.getByTestId('identity-attribute-add')).toBeDisabled()
  await expect(page.getByTestId('identity-attribute-why')).toHaveText('This group already gives that value.')

  // the home's own refusal is shown in its words (here a caller without the role), and nothing is listed for it
  await page.getByTestId('identity-attribute-group').fill('CORP\\Nobody')
  await page.getByTestId('identity-attribute-value').fill('north')
  await page.getByTestId('identity-attribute-add').click()
  await expect(page.getByTestId('identity-attribute-refused')).toContainText('needs the admin role')
  expect(mock.mappings.map((m) => m.value)).toEqual(['east', 'west'])

  // remove one: by its id, and it is gone from the list and the summary
  await page.getByTestId('identity-attribute-region-east').getByTestId('identity-attribute-remove').click()
  await expect(page.getByTestId('identity-attribute-region-east')).toHaveCount(0)
  await expect(page.getByTestId('identity-attribute-summary-region')).toHaveText('region: west')
  expect(mock.sent['DELETE attribute-mappings/m1']).toHaveLength(1)

  expect(outside).toEqual([])
}
