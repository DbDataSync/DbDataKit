import { parseYaml, toYaml } from '@dbdatakit/shell'

/** The traits a verb or action may carry, as the policy file spells them, with what each one means for a person reading the form. */
export const TRAITS: { name: string; meaning: string }[] = [
  { name: 'reads-definitions', meaning: 'reads definitions, files, metadata or the tool\'s own state' },
  { name: 'reads-data', meaning: 'reads rows or values of someone\'s data' },
  { name: 'writes-definitions', meaning: 'writes definitions or files' },
  { name: 'writes-data', meaning: 'writes rows or values' },
  { name: 'changes-structure', meaning: 'changes the structure of a database or a deployment' },
  { name: 'deletes', meaning: 'deletes something a command cannot bring back' },
  { name: 'outward', meaning: 'sends something outside the suite' },
  { name: 'elevated', meaning: 'holds access the person does not' },
]

export type Setting = '' | 'ask' | 'ask-once' | 'do-not-ask'
export const SETTINGS: Setting[] = ['', 'ask', 'ask-once', 'do-not-ask']

export const settingLabel = (s: Setting): string => ({ '': 'no minimum', ask: 'ask every time', 'ask-once': 'ask once per conversation', 'do-not-ask': 'do not ask' })[s]

/** What a minimum means in a sentence. */
export const settingMeaning = (trait: string, s: Setting): string => {
  const what = TRAITS.find((t) => t.name === trait)?.meaning ?? trait
  return { '': `Nothing is required for a tool that ${what}: the tool's own default applies.`, ask: `A tool that ${what} always asks the person first, whatever an agent says.`, 'ask-once': `A tool that ${what} asks once per conversation, whatever an agent says.`, 'do-not-ask': `A tool that ${what} may run without asking (it can still be asked of by an agent or by name).` }[s]
}

export interface LimitForm { turns: string; toolCalls: string; tokens: string; cost: string }
export type DataRule = 'allow' | 'ask' | 'refuse'

export interface PolicyForm {
  minimums: Record<string, Setting>
  tools: { name: string; setting: Setting }[]
  conversation: LimitForm
  person: LimitForm
  install: LimitForm
  notifyAt: string
  cloudReadsData: DataRule
}

const noLimit = (): LimitForm => ({ turns: '', toolCalls: '', tokens: '', cost: '' })

export const emptyPolicy = (): PolicyForm => ({ minimums: {}, tools: [], conversation: noLimit(), person: noLimit(), install: noLimit(), notifyAt: '80', cloudReadsData: 'allow' })

type Dict = Record<string, unknown>
const asDict = (v: unknown): Dict => (v && typeof v === 'object' && !Array.isArray(v) ? (v as Dict) : {})
const text = (v: unknown): string => (v === undefined || v === null ? '' : String(v))

function readLimits(v: unknown): LimitForm {
  const d = asDict(v)
  return { turns: text(d['turns']), toolCalls: text(d['tool-calls']), tokens: text(d['tokens']), cost: text(d['cost']) }
}

/** The form for a policy file's text; null (with the parser's words) when the text is not YAML. */
export function policyFromYaml(yaml: string): { form: PolicyForm | null; errors: string[] } {
  const parsed = parseYaml(yaml)
  if (parsed.errors.length > 0) return { form: null, errors: parsed.errors }
  const root = asDict(parsed.value)
  const prompting = asDict(root['prompting'])
  const minimums: Record<string, Setting> = {}
  for (const [trait, setting] of Object.entries(asDict(prompting['minimums']))) minimums[trait] = text(setting) as Setting
  const tools = Object.entries(asDict(prompting['tools'])).map(([name, setting]) => ({ name, setting: text(setting) as Setting }))
  const limits = asDict(root['limits'])
  const cloud = asDict(asDict(root['data-rules'])['cloud-providers'])
  return {
    form: {
      minimums, tools, conversation: readLimits(limits['conversation']), person: readLimits(limits['person']), install: readLimits(limits['install']),
      notifyAt: limits['notify-at'] === undefined ? '80' : text(limits['notify-at']), cloudReadsData: (text(cloud['reads-data']) || 'allow') as DataRule,
    },
    errors: [],
  }
}

function limitsOut(l: LimitForm): Dict | undefined {
  const out: Dict = {}
  const whole = (v: string) => (v.trim() === '' ? undefined : Number(v))
  if (whole(l.turns) !== undefined) out['turns'] = whole(l.turns)
  if (whole(l.toolCalls) !== undefined) out['tool-calls'] = whole(l.toolCalls)
  if (whole(l.tokens) !== undefined) out['tokens'] = whole(l.tokens)
  if (whole(l.cost) !== undefined) out['cost'] = whole(l.cost)
  return Object.keys(out).length > 0 ? out : undefined
}

/** The policy file the form makes (`agent-policy: 1`, then only what is set). The server reads it and says what it changes: the form never decides what is allowed. */
export function policyToYaml(form: PolicyForm): string {
  const root: Dict = { 'agent-policy': 1 }
  const minimums: Dict = {}
  for (const t of TRAITS) if (form.minimums[t.name]) minimums[t.name] = form.minimums[t.name]
  const tools: Dict = {}
  for (const t of form.tools) if (t.name.trim() && t.setting) tools[t.name.trim()] = t.setting
  if (Object.keys(minimums).length > 0 || Object.keys(tools).length > 0) {
    const prompting: Dict = {}
    if (Object.keys(minimums).length > 0) prompting['minimums'] = minimums
    if (Object.keys(tools).length > 0) prompting['tools'] = tools
    root['prompting'] = prompting
  }
  const limits: Dict = {}
  for (const [name, l] of [['conversation', form.conversation], ['person', form.person], ['install', form.install]] as const) {
    const out = limitsOut(l)
    if (out) limits[name] = out
  }
  if (form.notifyAt.trim() !== '' && form.notifyAt.trim() !== '80') limits['notify-at'] = Number(form.notifyAt)
  if (Object.keys(limits).length > 0) root['limits'] = limits
  if (form.cloudReadsData !== 'allow') root['data-rules'] = { 'cloud-providers': { 'reads-data': form.cloudReadsData } }
  return toYaml(root) + '\n'
}
