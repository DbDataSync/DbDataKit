import { test as base, expect, type Page } from '@playwright/test'
import { execFileSync, spawn, type ChildProcess } from 'node:child_process'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
export const buildDll = path.resolve(here, '../../src/DbDataBuild.Cli/bin/Debug/net10.0/dbdatabuild.dll')

/** The text of the page's main area, whitespace folded, including what a closed `<details>` holds (the statements of a plan). */
export async function screenText(page: Page): Promise<string> {
  const main = page.locator('main#view')
  await expect(main).toBeVisible()
  return ((await main.textContent()) ?? '').replace(/\s+/g, ' ')
}

/** A project made from the starter template, and a `dbdatabuild ui mcp` over it that mints the one-time links of its page. */
export class BuildPage {
  readonly directory: string
  private child!: ChildProcess
  private buffer = ''
  private nextId = 2
  private readonly waiting = new Map<number, (message: any) => void>()

  constructor(readonly allowApply = false) {
    this.directory = fs.mkdtempSync(path.join(os.tmpdir(), 'ddb-page-'))
    fs.rmSync(this.directory, { recursive: true, force: true })
    execFileSync('dotnet', ['exec', buildDll, 'project', 'create', 'starter', this.directory, '--format', 'json'], { stdio: 'pipe' })
  }

  async start() {
    const args = ['exec', buildDll, 'ui', 'mcp', '--project', this.directory, ...(this.allowApply ? ['--allow-apply'] : [])]
    this.child = spawn('dotnet', args, { stdio: ['pipe', 'pipe', 'pipe'] })
    this.child.stdout!.on('data', chunk => {
      this.buffer += chunk
      let i
      while ((i = this.buffer.indexOf('\n')) >= 0) {
        const line = this.buffer.slice(0, i).trim()
        this.buffer = this.buffer.slice(i + 1)
        if (!line) continue
        const message = JSON.parse(line)
        this.waiting.get(message.id)?.(message)
      }
    })
    this.child.stderr!.resume()
    // no MCP Apps advertised: `show` then answers with a link to the page in a browser
    await this.request(1, 'initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'playwright', version: '1' } })
    this.child.stdin!.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n')
  }

  private request(id: number, method: string, params: object): Promise<any> {
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error(`ui mcp did not answer ${method}`)), 60_000)
      this.waiting.set(id, m => { clearTimeout(timer); this.waiting.delete(id); resolve(m) })
      this.child.stdin!.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n')
    })
  }

  /** A new link, which works once. */
  async link(screen = 'health'): Promise<string> {
    const answer = await this.request(this.nextId++, 'tools/call', { name: 'show', arguments: { screen } })
    const url = answer.result?.structuredContent?.url
    if (!url) throw new Error('ui mcp gave no link: ' + JSON.stringify(answer))
    return url
  }

  /** Opens the page (the link is spent, the browser keeps the session) and shows a screen, by the page's own route, e.g. `#/lineage`. */
  async open(page: Page, route = '#/') {
    await page.goto(await this.link())
    // the screen the link opens on is drawn first; a route set while it is still running would be overwritten by it
    await expect(page.locator('main#view')).toContainText('tests passed')
    await page.evaluate(r => { location.hash = r }, route)
    await expect(page.locator('main#view')).toBeVisible()
  }

  async stop() {
    try { this.child?.stdin?.end(); this.child?.kill() } catch { /* already gone */ }
    fs.rmSync(this.directory, { recursive: true, force: true })
  }

  /** The first model file of the project, and its dotted name (`marts.customers`). */
  modelFile(): string {
    const root = path.join(this.directory, 'models')
    const walk = (d: string): string[] => fs.readdirSync(d, { withFileTypes: true }).flatMap(e => e.isDirectory() ? walk(path.join(d, e.name)) : e.name.endsWith('.sql') ? [path.join(d, e.name)] : [])
    return walk(root).sort()[0]
  }

  firstModel(): string {
    return path.relative(path.join(this.directory, 'models'), this.modelFile()).slice(0, -4).split(path.sep).join('.')
  }

  /** Puts a plan fixture (made by `PlanDocument.Serialize`, hash included) into the project's plans folder; `edit` changes it afterwards, which is what an edit by hand does. */
  addPlan(id: string, edit?: (text: string) => string, report?: string) {
    const folder = path.join(this.directory, 'plans', 'postgres')
    fs.mkdirSync(folder, { recursive: true })
    const text = fs.readFileSync(path.join(here, 'fixtures', `${id}.plan.yml`), 'utf8')
    fs.writeFileSync(path.join(folder, `${id}.plan.yml`), edit ? edit(text) : text)
    if (report) fs.writeFileSync(path.join(folder, `${id}.plan.md`), report)
    return 'plans/postgres/' + id + '.plan.yml'
  }
}

export { expect }
export const test = base
