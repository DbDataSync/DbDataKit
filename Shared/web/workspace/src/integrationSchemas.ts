import { appendAt, parseYaml, type SchemaNode } from '@dbdatakit/shell'

/** The editor of an integration project (plan 27a IX24, docs/integrations.md): the manifest `package.yaml` as a form made from this schema or as YAML, the job classes of `src/` and the test scripts of `tests/` as code. */
export const INTEGRATION_MANIFEST_SCHEMA = 'integration-manifest.schema.json'
export const MANIFEST_PATH = 'package.yaml'

const schedule: SchemaNode = {
  type: 'object',
  description: 'When the task runs by itself: a cron expression (five fields, minute first) or a fixed interval. Leave it out for a task that runs only by a trigger or by hand.',
  properties: {
    cron: { description: 'A cron expression, for example `0 2 * * *` (every day at 02:00).', type: 'string' },
    every: { description: 'An interval instead of a cron: a number and a unit (`15m`, `2h`).', type: 'string' },
    time_zone: { description: 'The time zone the cron is read in (`Europe/Berlin`); the runner\'s own when left out.', type: 'string' },
    misfire: { description: 'What happens to a run that was due while no runner was up.', enum: ['skip', 'fire_once'] },
    misfire_grace: { description: 'How late a missed run may still start (`5m`).', type: 'string' },
  },
}

const limits: SchemaNode = {
  type: 'object',
  description: 'What a run of the task may use, and how it is retried.',
  properties: {
    timeout: { description: 'How long a run may last (`30m`).', type: 'string' },
    memory_mb: { description: 'Memory the process may use, in megabytes.', type: 'integer', minimum: 1 },
    cpu: { description: 'CPU cores the process may use.', type: 'number' },
    concurrency: { description: 'How many runs of this task may be at once.', type: 'integer', minimum: 1 },
    attempts: { description: 'How many times a run is tried when it fails for a temporary reason.', type: 'integer', minimum: 1 },
    backoff: { description: 'The wait before the first retry (`30s`).', type: 'string' },
    max_backoff: { description: 'The longest wait between retries (`10m`).', type: 'string' },
  },
}

const task: SchemaNode = {
  type: 'object',
  required: ['name'],
  description: 'A task: the work a job class does, with its schedule, inputs, connection slots and limits.',
  properties: {
    name: { description: 'The task\'s name; the job class carries the same in `[RunnerTask("name")]`.', type: 'string' },
    entry: { type: 'object', description: 'Which class runs it.', properties: { type: { description: 'The full name of the job class (`Integration.Hello`).', type: 'string' }, method: { description: 'The method, `Run` when left out.', type: 'string' } } },
    schedule,
    inputs: {
      type: 'array',
      description: 'Values a run is given (by hand, or the default).',
      items: { type: 'object', required: ['name', 'type'], properties: { name: { type: 'string' }, type: { description: 'text, int, decimal, bool, date, time or datetime.', type: 'string' }, default: { description: 'Used when the run is not given one.' } } },
    },
    connections: {
      type: 'array',
      description: 'The connection slots the task opens. Each is bound to a connection of the channel on the Runner; the code never holds a secret.',
      items: { type: 'object', required: ['name', 'kind'], properties: { name: { description: 'The slot\'s name.', type: 'string' }, kind: { description: 'What the connection is: `database`, `http`, `folder`.', type: 'string' } } },
    },
    limits,
    requires: { type: 'array', description: 'What the runner must provide (`network:outside`).', items: { type: 'string' } },
  },
}

/** The shape of `package.yaml` for the form. The Runner's strict reader is the authority and its words are what the check shows; this schema only says which settings exist. Triggers and the progress declaration are left to the YAML tab. */
export const integrationManifestSchema: SchemaNode = {
  $id: INTEGRATION_MANIFEST_SCHEMA,
  type: 'object',
  description: 'An integration for the Runner: the package, how it starts, and its tasks.',
  required: ['schema', 'name', 'launch', 'tasks'],
  properties: {
    schema: { description: 'The format of the file; always `package/1`.', enum: ['package/1'] },
    name: { description: 'The package\'s name on the Runner: lower-case letters, digits, `-` and `_`.', type: 'string' },
    description: { description: 'A sentence for the people who look for it.', type: 'string' },
    launch: {
      type: 'object',
      description: 'How the runner starts it. A csx project writes `bin/<name>.dll` and says `kind: assembly`.',
      properties: { kind: { enum: ['assembly', 'executable'] }, assembly: { description: 'The assembly under `bin/` that the build writes.', type: 'string' }, command: { description: 'For an executable: the program.', type: 'string' } },
    },
    built_against: { type: 'object', description: 'What the package was built for; the Runner checks it.', properties: { dotnet: { type: 'string' }, kit: { type: 'string' }, protocol: { type: 'string' } } },
    tasks: { type: 'array', minItems: 1, description: 'The tasks of the package.', items: task },
  },
}

/** The names a project's files are sorted into for the overview. */
export function integrationFilesOf(paths: readonly string[]): { code: string[]; tests: string[]; other: string[] } {
  const code = paths.filter((p) => /^src\/.+\.csx$/.test(p)).sort()
  const tests = paths.filter((p) => /^tests\/.+\.csx$/.test(p)).sort()
  return { code, tests, other: paths.filter((p) => !code.includes(p) && !tests.includes(p)) }
}

export interface TaskLine { name: string; when: string; slots: number }

/** The tasks of a manifest as the overview lists them, with how each is started; empty when the text does not parse (the form and the check say why). */
export function tasksOf(manifest: string): TaskLine[] {
  const parsed = parseYaml(manifest)
  const tasks = (parsed.value as { tasks?: unknown } | undefined)?.tasks
  if (!Array.isArray(tasks)) return []
  return tasks.filter((t): t is Record<string, unknown> => typeof t === 'object' && t !== null && typeof (t as { name?: unknown }).name === 'string').map((t) => {
    const s = t.schedule as { cron?: string; every?: string } | undefined
    const triggers = Array.isArray(t.triggers) ? t.triggers.length : 0
    const when = s?.cron ? `cron ${s.cron}` : s?.every ? `every ${s.every}` : triggers > 0 ? `${triggers} trigger${triggers === 1 ? '' : 's'}` : 'by hand'
    return { name: t.name as string, when, slots: Array.isArray(t.connections) ? t.connections.length : 0 }
  })
}

const taskName = /^[a-z0-9][a-z0-9_-]{0,63}$/

/** Why a new task cannot have this name, or null. */
export function taskNameProblem(name: string, existing: readonly string[]): string | null {
  if (name === '') return null
  if (!taskName.test(name)) return 'Lower-case letters, digits, - and _, starting with a letter or digit.'
  if (existing.includes(name)) return `The task ${name} already exists.`
  return null
}

/** The C# class name of a task: `pull-orders` is `PullOrders`. */
export function classNameOf(name: string): string {
  const pascal = name.split(/[-_]/).filter(Boolean).map((p) => p[0].toUpperCase() + p.slice(1)).join('')
  return /^[A-Za-z]/.test(pascal) ? pascal : `Task${pascal}`
}

/** The files a new task adds: the manifest with the task appended, a job class in `src/` and a test in `tests/`; written together so the check never sees a task with no class. */
export function newTaskFiles(manifest: string, name: string): { path: string; text: string }[] {
  const cls = classNameOf(name)
  const job = [
    `namespace Integration;`, '',
    `public static class ${cls}Counters { public static readonly Counter Done = new("done"); }`, '',
    `[RunnerTask("${name}")]`, `public sealed class ${cls} : RunnerJob<NoInput, NoProgress, NoSlots>`, '{',
    '    protected override Task RunAsync(CancellationToken ct)', '    {', `        Count(${cls}Counters.Done, 1);`, '        return Task.CompletedTask;', '    }', '}', '',
  ].join('\n')
  const test = [
    `var sim = new RunSimulator<Integration.${cls}>();`, 'await sim.RunAsync();',
    'True(sim.Outcome == DbDataKit.Monitoring.RunOutcome.Succeeded, "the run did not succeed");', `Equal(1L, sim.Counted(Integration.${cls}Counters.Done));`, 'sim.Dispose();', '',
  ].join('\n')
  return [
    { path: MANIFEST_PATH, text: appendAt(manifest, ['tasks'], { name, entry: { type: `Integration.${cls}` }, triggers: [{ manual: true }] }) },
    { path: `src/${name}.csx`, text: job },
    { path: `tests/${name}.csx`, text: test },
  ]
}
