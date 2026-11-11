import { createContext, useContext, useMemo, useState, type ReactNode } from 'react'
import { Field } from '../components/Field'
import { defaultFor, typesOf } from './defaults'
import type { FieldError, Loc, SchemaNode, SchemaSet } from './schemaSet'
import { appendAt, moveIn, parseYaml, removeAt, setAt, toYaml, type YamlPath } from './yamlText'

/** What a node of the form is told: where it is in the document and in the schema, and what the document holds there. */
export interface NodeContext {
  path: YamlPath
  loc: Loc
  node: SchemaNode
  value: unknown
}

export interface NodeProps extends NodeContext {
  label: string
  required: boolean
  /** Draws any other node, so an override can wrap or contain the generic rendering of a part. */
  renderNode: (ctx: { path: YamlPath; loc: Loc; value: unknown; label: string; required: boolean }) => ReactNode
  /** Changes the document at a path (the whole text is rewritten; only that part moves). */
  set: (path: YamlPath, value: unknown) => void
  remove: (path: YamlPath) => void
}

/**
 * A better screen for one part than the generic one: the first override whose `match` says yes draws the node instead. The generic renderer knows only what a schema says (strings, numbers, lists, mappings, one of several shapes);
 * a part that deserves a picker, a preview or a different layout (a column's type, a model's kind) gets an override from the product that has the knowledge, and the rest of the form is unchanged.
 */
export interface FormOverride {
  match: (ctx: NodeContext) => boolean
  render: (props: NodeProps) => ReactNode
}

interface Env {
  schemas: SchemaSet
  problems: FieldError[]
  overrides: FormOverride[]
  set: (path: YamlPath, value: unknown) => void
  remove: (path: YamlPath) => void
  append: (path: YamlPath, value: unknown) => void
  move: (path: YamlPath, from: number, to: number) => void
}

const FormEnv = createContext<Env | null>(null)
const useEnv = () => useContext(FormEnv)!

const sameInt = (a: YamlPath, b: YamlPath) => a.length === b.length && a.every((s, i) => s === b[i])

const firstSentence = (text?: string) => (text ?? '').split(/(?<=\.)\s/)[0]

function Problems({ path }: { path: YamlPath }) {
  const { problems } = useEnv()
  const here = problems.filter((p) => sameInt(p.path, path))
  if (here.length === 0) return null
  return <>{here.map((p, i) => <div key={i} className="hint warn" data-testid="form-problem">{p.message}</div>)}</>
}

/** The part of the document no form covers (a shape the schema leaves open, or one this renderer does not draw): its YAML, edited as text and written back when it parses. */
function RawNode({ path, value, label }: { path: YamlPath; value: unknown; label: string }) {
  const { set } = useEnv()
  const original = toYaml(value)
  const [draft, setDraft] = useState(original)
  const [seen, setSeen] = useState(original)
  const [error, setError] = useState<string | null>(null)
  if (seen !== original) { setSeen(original); setDraft(original) }      // the document changed elsewhere (the YAML tab)
  return (
    <Field label={label}>
      <textarea
        className="input mono"
        rows={Math.min(12, Math.max(2, draft.split('\n').length))}
        value={draft}
        spellCheck={false}
        data-testid={`form-raw-${path.join('.')}`}
        onChange={(e) => setDraft(e.target.value)}
        onBlur={() => {
          if (draft === original) return
          const parsed = parseYaml(draft)
          if (parsed.errors.length > 0) { setError(parsed.errors[0]); return }
          setError(null)
          set(path, parsed.value ?? null)
        }}
      />
      {error && <div className="hint warn">{error}</div>}
      <Problems path={path} />
    </Field>
  )
}

function Scalar({ ctx, label, required }: { ctx: NodeContext; label: string; required: boolean }) {
  const { set, remove } = useEnv()
  const { node, path, value } = ctx
  const types = typesOf(node)
  const hint = firstSentence(node.description)
  const testId = `form-field-${path.join('.')}`
  const choices = node.enum ?? (node.const !== undefined ? [node.const] : undefined)

  let control: ReactNode
  if (choices) {
    control = (
      <select className="input" value={value === undefined ? '' : String(value)} data-testid={testId} onChange={(e) => (e.target.value === '' ? remove(path) : set(path, choices.find((c) => String(c) === e.target.value)))}>
        {!required && <option value="">—</option>}
        {required && value === undefined && <option value="">Choose…</option>}
        {choices.map((c) => <option key={String(c)} value={String(c)}>{String(c)}</option>)}
      </select>
    )
  } else if (types.length > 0 && types.every((t) => t === 'boolean')) {
    control = <input type="checkbox" checked={value === true} data-testid={testId} onChange={(e) => set(path, e.target.checked)} />
  } else if (types.includes('number') || types.includes('integer')) {
    control = <input className="input" inputMode="decimal" value={value === undefined || value === null ? '' : String(value)} data-testid={testId} onChange={(e) => (e.target.value === '' ? remove(path) : set(path, Number.isNaN(Number(e.target.value)) ? e.target.value : Number(e.target.value)))} />
  } else {
    control = <input className="input" value={value === undefined || value === null ? '' : String(value)} data-testid={testId} onChange={(e) => (e.target.value === '' && !required ? remove(path) : set(path, e.target.value))} />
  }
  return (
    <Field label={<>{label}{required && <span title="required"> *</span>}</>}>
      {hint && <span className="hint" title={node.description}>{hint}</span>}
      {control}
      <Problems path={path} />
    </Field>
  )
}

function ObjectNode({ ctx, label, required }: { ctx: NodeContext; label: string; required: boolean }) {
  const { schemas, set, remove } = useEnv()
  const { node, loc, path, value } = ctx
  const object = value !== null && typeof value === 'object' && !Array.isArray(value) ? (value as Record<string, unknown>) : {}
  const known = Object.keys(node.properties ?? {})
  const other = Object.keys(object).filter((k) => !known.includes(k))
  // the `key=`, `key+` and `key-` spellings of a setting (layering, in files that inherit) are not offered; one already in the file is drawn
  const absent = known.filter((k) => !(k in object) && !(node.required ?? []).includes(k) && !/[=+-]$/.test(k))
  const open = known.filter((k) => k in object || (node.required ?? []).includes(k))
  const mapSchema = typeof node.additionalProperties === 'object' ? node.additionalProperties : null
  const [newKey, setNewKey] = useState('')
  const isRoot = path.length === 0
  const hint = firstSentence(node.description)

  const body = (
    <>
      {open.map((key) => (
        <FormNode key={key} path={[...path, key]} loc={schemas.child(loc, 'properties', key)} value={object[key]} label={key} required={(node.required ?? []).includes(key)} />
      ))}
      {other.map((key) => (
        mapSchema
          ? <div key={key} className="row" style={{ gap: 8, alignItems: 'flex-start' }}>
              <div style={{ flex: 1 }}><FormNode path={[...path, key]} loc={schemas.child(loc, 'additionalProperties')} value={object[key]} label={key} required /></div>
              <button type="button" className="btn btn-sm" aria-label={`Remove ${key}`} data-testid={`form-remove-${[...path, key].join('.')}`} onClick={() => remove([...path, key])}>Remove</button>
            </div>
          : <div key={key} className="row" style={{ gap: 8, alignItems: 'flex-start' }}>
              <div style={{ flex: 1 }}><RawNode path={[...path, key]} value={object[key]} label={key} /></div>
              <button type="button" className="btn btn-sm" aria-label={`Remove ${key}`} onClick={() => remove([...path, key])}>Remove</button>
            </div>
      ))}
      {absent.length > 0 && (
        <div className="row" style={{ gap: 6, flexWrap: 'wrap', alignItems: 'center' }} data-testid={`form-adders-${path.join(".")}`}>
          <span className="hint">Add:</span>
          {absent.map((key) => (
            <button key={key} type="button" className="btn btn-sm" title={node.properties?.[key]?.description} data-testid={`form-add-${[...path, key].join('.')}`}
              onClick={() => set([...path, key], defaultFor(schemas, schemas.child(loc, 'properties', key)) ?? '')}>
              {key}
            </button>
          ))}
        </div>
      )}
      {mapSchema && (
        <div className="row" style={{ gap: 6 }}>
          <input className="input sm" placeholder="name" value={newKey} aria-label="New name" data-testid={`form-newkey-${path.join('.')}`} onChange={(e) => setNewKey(e.target.value)} />
          <button type="button" className="btn btn-sm" disabled={newKey.trim() === '' || newKey.trim() in object} data-testid={`form-addkey-${path.join('.')}`}
            onClick={() => { set([...path, newKey.trim()], defaultFor(schemas, schemas.child(loc, 'additionalProperties')) ?? ''); setNewKey('') }}>
            Add
          </button>
        </div>
      )}
      <Problems path={path} />
    </>
  )
  if (isRoot) return <div style={{ display: 'grid', gap: 12 }}>{body}</div>
  return (
    <fieldset className="card" style={{ padding: 10, margin: 0 }} data-testid={`form-object-${path.join('.')}`}>
      <legend className="hint" style={{ padding: '0 6px' }} title={node.description}>{label}{required && ' *'}{hint && ` — ${hint}`}</legend>
      <div style={{ display: 'grid', gap: 10 }}>{body}</div>
    </fieldset>
  )
}

function ArrayNode({ ctx, label, required }: { ctx: NodeContext; label: string; required: boolean }) {
  const { schemas, append, remove, move } = useEnv()
  const { node, loc, path, value } = ctx
  const list = Array.isArray(value) ? value : []
  const itemLoc = schemas.child(loc, 'items')
  const hint = firstSentence(node.description)
  return (
    <fieldset className="card" style={{ padding: 10, margin: 0 }} data-testid={`form-list-${path.join('.')}`}>
      <legend className="hint" style={{ padding: '0 6px' }} title={node.description}>{label}{required && ' *'}{hint && ` — ${hint}`}</legend>
      <div style={{ display: 'grid', gap: 10 }}>
        {list.map((item, i) => (
          <div key={i} className="row" style={{ gap: 8, alignItems: 'flex-start' }}>
            <div style={{ flex: 1 }}><FormNode path={[...path, i]} loc={itemLoc} value={item} label={`${label} ${i + 1}`} required /></div>
            <div className="row" style={{ gap: 4 }}>
              <button type="button" className="btn btn-sm" disabled={i === 0} aria-label={`Move ${label} ${i + 1} up`} onClick={() => move(path, i, i - 1)}>↑</button>
              <button type="button" className="btn btn-sm" disabled={i === list.length - 1} aria-label={`Move ${label} ${i + 1} down`} onClick={() => move(path, i, i + 1)}>↓</button>
              <button type="button" className="btn btn-sm" aria-label={`Remove ${label} ${i + 1}`} data-testid={`form-remove-${[...path, i].join('.')}`} onClick={() => remove([...path, i])}>Remove</button>
            </div>
          </div>
        ))}
        <div><button type="button" className="btn btn-sm" data-testid={`form-append-${path.join('.')}`} onClick={() => append(path, defaultFor(schemas, itemLoc) ?? '')}>Add</button></div>
        <Problems path={path} />
      </div>
    </fieldset>
  )
}

/** Several shapes are allowed here: the one the value fits is drawn, with a choice of the others. The choice replaces the value with that shape's start. */
function VariantNode({ ctx, label, required, variants }: { ctx: NodeContext; label: string; required: boolean; variants: Loc[] }) {
  const { schemas, set } = useEnv()
  const { value, path } = ctx
  const described = variants.map((v) => ({ loc: v, node: schemas.at(v).node }))
  const fitting = described.findIndex((v) => schemas.fits(v.loc, value))
  const name = (v: { node: SchemaNode }, i: number) => v.node.title ?? (typesOf(v.node)[0] ?? (v.node.const !== undefined ? String(v.node.const) : `shape ${i + 1}`))
  if (value === undefined) {
    return (
      <Field label={label}>
        <select className="input" value="" data-testid={`form-variant-${path.join('.')}`} onChange={(e) => e.target.value !== '' && set(path, defaultFor(schemas, described[Number(e.target.value)].loc) ?? '')}>
          <option value="">Choose…</option>
          {described.map((v, i) => <option key={i} value={i}>{name(v, i)}</option>)}
        </select>
      </Field>
    )
  }
  if (fitting < 0) return <RawNode path={path} value={value} label={label} />
  const chosen = described[fitting]
  return (
    <div style={{ display: 'grid', gap: 6 }}>
      {described.length > 1 && (
        <Field label={<>{label} — shape{required && ' *'}</>}>
          <select className="input" value={fitting} data-testid={`form-variant-${path.join('.')}`} onChange={(e) => set(path, defaultFor(schemas, described[Number(e.target.value)].loc) ?? '')}>
            {described.map((v, i) => <option key={i} value={i}>{name(v, i)}</option>)}
          </select>
        </Field>
      )}
      <FormNode path={path} loc={chosen.loc} value={value} label={label} required={required} />
    </div>
  )
}

interface FormNodeProps {
  path: YamlPath
  loc: Loc
  value: unknown
  label: string
  required: boolean
}

/** One part of the document, drawn from the schema at its place. */
export function FormNode({ path, loc, value, label, required }: FormNodeProps): ReactNode {
  const env = useEnv()
  const { schemas } = env
  let { node, loc: real } = schemas.at(loc)

  // `if`/`then`/`else` at a node: the document decides which branch applies
  if (node.if && (node.then || node.else)) {
    const branch = schemas.fits(schemas.child(real, 'if'), value) ? 'then' : 'else'
    if (node[branch]) return <FormNode path={path} loc={schemas.child(real, branch)} value={value} label={label} required={required} />
  }

  const ctx: NodeContext = { path, loc: real, node, value }
  const override = env.overrides.find((o) => o.match(ctx))
  if (override) {
    return override.render({
      ...ctx, label, required, set: env.set, remove: env.remove,
      renderNode: (c) => <FormNode path={c.path} loc={c.loc} value={c.value} label={c.label} required={c.required} />,
    })
  }

  // a `oneOf`/`anyOf` beside a shape of the node's own (a case that needs `expect` or `assert`) is a rule about that shape; one alone is a choice between shapes
  const variants = node.type === undefined && node.properties === undefined && node.items === undefined ? (node.oneOf ?? node.anyOf) : undefined
  if (variants) return <VariantNode ctx={ctx} label={label} required={required} variants={variants.map((_, i) => schemas.child(real, node.oneOf ? 'oneOf' : 'anyOf', i))} />

  const types = typesOf(node)
  if (types.includes('object') && value !== undefined && (value === null || typeof value === 'object') && !Array.isArray(value)) return <ObjectNode ctx={ctx} label={label} required={required} />
  if (types.includes('object') && value === undefined) return <ObjectNode ctx={ctx} label={label} required={required} />
  if (types.includes('array') && (value === undefined || Array.isArray(value))) return <ArrayNode ctx={ctx} label={label} required={required} />
  if (types.length === 0 && node.enum === undefined && node.const === undefined) return <RawNode path={path} value={value} label={label} />
  if (value !== undefined && value !== null && typeof value === 'object') return <RawNode path={path} value={value} label={label} />
  return <Scalar ctx={ctx} label={label} required={required} />
}

export interface SchemaFormProps {
  schemas: SchemaSet
  /** The file's schema, by name (`model.schema.json`). */
  schema: string
  /** The file's text; the form edits it through `onText` and never holds a copy of its own. */
  text: string
  onText: (text: string) => void
  overrides?: FormOverride[]
}

/**
 * A form for a YAML file, drawn from its JSON Schema (plan 13, W05). Every change rewrites the text with the `yaml` package's document model, so comments and the rest of the file are kept and the YAML tab shows the same file.
 * What the form cannot draw is shown as YAML in place; what a product wants drawn better is an override.
 */
export function SchemaForm({ schemas, schema, text, onText, overrides = [] }: SchemaFormProps) {
  const parsed = useMemo(() => parseYaml(text), [text])
  const [failure, setFailure] = useState<string | null>(null)
  const problems = useMemo(() => (parsed.errors.length > 0 ? [] : schemas.problems(schema, parsed.value ?? {})), [schemas, schema, parsed])

  const run = (change: (t: string) => string) => {
    try { onText(change(text)); setFailure(null) } catch (e) { setFailure(e instanceof Error ? e.message : String(e)) }
  }
  const env: Env = useMemo(() => ({
    schemas, problems, overrides,
    set: (path, value) => run((t) => setAt(t, path, value)),
    remove: (path) => run((t) => removeAt(t, path)),
    append: (path, value) => run((t) => appendAt(t, path, value)),
    move: (path, from, to) => run((t) => moveIn(t, path, from, to)),
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }), [schemas, problems, overrides, text, onText])

  if (parsed.errors.length > 0) {
    return <div className="banner error" role="alert" data-testid="form-unparsed"><span className="mark">!</span><span>The YAML does not parse, so there is no form to show: {parsed.errors[0]}. Fix it in the YAML tab.</span></div>
  }
  return (
    <FormEnv.Provider value={env}>
      <div data-testid="schema-form">
        {failure && <div className="banner error" role="alert"><span className="mark">!</span><span>{failure}</span></div>}
        <FormNode path={[]} loc={{ base: schema, pointer: '' }} value={parsed.value ?? {}} label="" required />
      </div>
    </FormEnv.Provider>
  )
}
