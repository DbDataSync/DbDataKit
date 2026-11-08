import type { ReactNode } from 'react'
import type { ParameterValues } from '../views/model'
import { isSafeTextLink } from '../views/common'
import { MARKDOWN_IMAGE, MARKDOWN_LINK, RAW_HTML } from '../views/widgets'

/**
 * The markdown of a `text` widget (plan 32 section 2): the safe subset the portal home has (headings, lists, bold, italic, `code`, links), **no raw HTML, no image, links only to a path on the host**
 * (`/docs/x`; the portal home also allows `https://`, a widget does not). The refusals are those of `DBD-APP-013` plus the image: `unsafeTextReasons` finds them (the loaders refuse such a file at load; the
 * renderer refuses it again, so text that got here some other way draws as an error and nothing of it). `{{params.name}}` is substituted **as text**, after the markdown is read, so a value never becomes markup
 * or a link, and never goes into a link's target.
 */

export const UNSAFE_TEXT_CODE = 'DBD-VIEW-002'

/** Why a text may not be drawn; empty when it may. */
export function unsafeTextReasons(text: string): string[] {
  const reasons: string[] = []
  for (const line of text.split('\n')) {
    if (RAW_HTML.test(line)) reasons.push('Text has no HTML or script: this looks like a tag.')
    if (MARKDOWN_IMAGE.test(line)) reasons.push('Text carries no image: use an `image` widget, which holds the picture by hash.')
    for (const m of line.matchAll(MARKDOWN_LINK)) {
      const index = m.index ?? 0
      if (index > 0 && line[index - 1] === '!') continue
      if (!isSafeTextLink(m[1]!)) reasons.push(`\`${m[1]}\` is not a link text may carry: only a path on the host that starts with a single \`/\`.`)
    }
  }
  return [...new Set(reasons)]
}

/** A parameter's value as the text a person reads: a list joined, nothing as empty. */
export function parameterText(value: ParameterValues[string] | undefined): string {
  if (value === undefined || value === null) return ''
  return Array.isArray(value) ? value.join(', ') : String(value)
}

const PARAM = /\{\{\s*params\.([A-Za-z0-9_-]+)\s*\}\}/g
export const substituteParameters = (text: string, parameters: ParameterValues): string => text.replace(PARAM, (_m, name: string) => parameterText(parameters[name]))

type Block =
  | { kind: 'heading'; level: 2 | 3 | 4; text: string }
  | { kind: 'paragraph'; text: string }
  | { kind: 'list'; ordered: boolean; items: string[] }

const HEADING = /^(#{1,3})\s+(.*)$/
const BULLET = /^\s*[-*]\s+(.*)$/
const NUMBERED = /^\s*\d+[.)]\s+(.*)$/

export function parseBlocks(text: string): Block[] {
  const blocks: Block[] = []
  let paragraph: string[] = []
  let list: { ordered: boolean; items: string[] } | null = null
  const flush = () => {
    if (paragraph.length > 0) blocks.push({ kind: 'paragraph', text: paragraph.join(' ') })
    if (list) blocks.push({ kind: 'list', ...list })
    paragraph = []
    list = null
  }
  for (const raw of text.split('\n')) {
    const line = raw.trimEnd()
    if (line.trim() === '') { flush(); continue }
    const heading = HEADING.exec(line)
    if (heading) { flush(); blocks.push({ kind: 'heading', level: (heading[1]!.length + 1) as 2 | 3 | 4, text: heading[2]!.trim() }); continue }
    const bullet = BULLET.exec(line)
    const numbered = bullet ? null : NUMBERED.exec(line)
    if (bullet || numbered) {
      const ordered = numbered !== null
      if (list && list.ordered !== ordered) flush()
      if (paragraph.length > 0) flush()
      list ??= { ordered, items: [] }
      list.items.push((bullet ?? numbered)![1]!.trim())
      continue
    }
    if (list) flush()
    paragraph.push(line.trim())
  }
  flush()
  return blocks
}

const INLINE = /\*\*([^*]+)\*\*|\*([^*\s][^*]*)\*|`([^`]+)`|\[([^\]]*)\]\(([^)]*)\)/g

function inline(text: string, parameters: ParameterValues, keyBase: string): ReactNode[] {
  const out: ReactNode[] = []
  const sub = (s: string) => substituteParameters(s, parameters)
  let last = 0
  let n = 0
  for (const m of text.matchAll(INLINE)) {
    const index = m.index ?? 0
    if (index > last) out.push(sub(text.slice(last, index)))
    const key = `${keyBase}.${n++}`
    if (m[1] !== undefined) out.push(<strong key={key}>{sub(m[1])}</strong>)
    else if (m[2] !== undefined) out.push(<em key={key}>{sub(m[2])}</em>)
    else if (m[3] !== undefined) out.push(<code key={key}>{sub(m[3])}</code>)
    else out.push(<a key={key} href={m[5]!}>{sub(m[4]!)}</a>)
    last = index + m[0].length
  }
  if (last < text.length) out.push(sub(text.slice(last)))
  return out
}

/** The blocks of a text as React elements; the text has passed `unsafeTextReasons` (the caller checks). */
export function renderMarkdown(text: string, parameters: ParameterValues = {}): ReactNode[] {
  return parseBlocks(text).map((block, i) => {
    const key = `b${i}`
    switch (block.kind) {
      case 'heading': {
        const Tag = `h${block.level}` as 'h2' | 'h3' | 'h4'
        return <Tag key={key}>{inline(block.text, parameters, key)}</Tag>
      }
      case 'paragraph': return <p key={key}>{inline(block.text, parameters, key)}</p>
      case 'list': {
        const Tag = block.ordered ? 'ol' : 'ul'
        return <Tag key={key}>{block.items.map((item, j) => <li key={j}>{inline(item, parameters, `${key}.${j}`)}</li>)}</Tag>
      }
    }
  })
}
