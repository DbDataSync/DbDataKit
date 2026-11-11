import { expect, test } from '@playwright/test'
import http from 'node:http'
import fs from 'node:fs'
import path from 'node:path'
import { scratchDirectory } from '../playwright.config'
import { signInNewAdmin } from './people'

// Plan 22a, GP18: the console's Delivery page. The console of this run is started with a `delivery:` binding in its bindings.yml (playwright.config.ts) that binds the channel `e2e-hook` to the connection `e2e-hook-conn`, a signed
// webhook at an address read from a file this story writes: a local listener. The story presses Test send and sees the message arrive signed, subscribes to a view, and checks the
// subscription says it cannot be sent from this console (no renderer here) while the History shows the test message with its try. Named to sort after `first-run` and `identity-home`.
test('a webhook channel is tested and arrives at a local listener, and a subscription is made and listed', async ({ page, context, baseURL }) => {
  const received: { signature: string | undefined; body: string }[] = []
  const server = http.createServer((req, res) => {
    let body = ''
    req.on('data', (c) => { body += c })
    req.on('end', () => { received.push({ signature: req.headers['x-dbdata-signature'] as string | undefined, body }); res.writeHead(200); res.end('ok') })
  })
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
  const port = (server.address() as { port: number }).port
  fs.writeFileSync(path.join(scratchDirectory, 'delivery-hook-url.txt'), `http://127.0.0.1:${port}/in/e2e-token`)
  fs.writeFileSync(path.join(scratchDirectory, 'delivery-hook-secret.txt'), 'e2e-signing-secret')
  try {
    await signInNewAdmin(page, context, baseURL!, 'Delivery Tester', `delivery-${Date.now()}-${Math.random().toString(36).slice(2, 8)}@example.com`)
    await page.goto('/delivery')
    await expect(page.getByTestId('delivery-page')).toBeVisible()
    await expect(page.getByTestId('delivery-outbox-e2e-hook')).toHaveAttribute('data-ready', 'true')
    await expect(page.getByText('e2e-token')).toHaveCount(0)          // the address carries a token: only the reference is shown

    await page.getByTestId('binding-test-e2e-hook-prod').click()
    await expect(page.getByTestId('binding-test-result-e2e-hook-prod')).toContainText('sent')
    expect(received).toHaveLength(1)
    expect(received[0].signature).toMatch(/^t=\d+,v1=[0-9a-f]{64}$/)
    expect(received[0].body).toContain('[TEST, prod] DbData test message')
    await expect(page.getByTestId('binding-row-e2e-hook-prod')).toContainText('(address hidden)')

    await page.getByTestId('delivery-tab-subscriptions').click()
    await page.getByTestId('subscription-new').click()
    await page.getByTestId('subscription-name').fill('Weekly sales')
    await page.getByTestId('subscription-view').fill('sales')
    await page.getByTestId('subscription-cron').fill('*/10 * * * *')
    await page.getByTestId('subscription-save').click()
    await expect(page.getByTestId('subscription-error')).toContainText('a subscription is at most hourly')
    await page.getByTestId('subscription-cron').fill('0 7 * * 1')
    await page.getByTestId('subscription-zone').fill('Europe/Berlin')
    await page.getByTestId('subscription-save').click()
    const row = page.getByTestId('subscription-Weekly sales')
    await expect(row).toContainText('every Monday at 07:00 (Europe/Berlin)')
    await expect(page.getByTestId('subscription-run-Weekly sales')).toBeDisabled()
    await page.getByTestId('subscription-pause-Weekly sales').click()
    await expect(row).toHaveAttribute('data-status', 'paused')
    await page.getByTestId('subscription-resume-Weekly sales').click()
    await expect(row).toHaveAttribute('data-status', 'active')

    await page.getByTestId('delivery-tab-history').click()
    await expect(page.getByTestId('history-row').filter({ hasText: 'DbData test message' })).toHaveAttribute('data-status', 'sent')
    await page.getByTestId('history-open').first().click()
    await expect(page.getByTestId('history-attempt').first()).toHaveAttribute('data-outcome', 'sent')
  } finally {
    await new Promise<void>((resolve) => server.close(() => resolve()))
  }
})
