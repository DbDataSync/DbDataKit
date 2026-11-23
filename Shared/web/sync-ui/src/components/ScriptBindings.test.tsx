import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { ScriptBindingsView } from './ScriptBindings'
import type { ScriptSlotInfo } from '../api/types'
import type { ScriptView } from '../types'

const slots: ScriptSlotInfo[] = [
  { slot: 'rowTransform', levels: ['connection', 'replication', 'mapping'], label: 'Row transform', description: 'Transforms a whole row, and may drop it.' },
  { slot: 'metadataProvider', levels: ['connection'], label: 'Metadata provider', description: 'Answers a connection\'s databases, tables and columns.' },
]
const script = (name: string, kind: string, enabled = true): ScriptView => ({ name, path: `config/scripts/${name}.yaml`, kind, language: 'CSharp', description: null, parameters: [], enabled })
const scripts = [script('mask-email', 'rowTransform'), script('old-mask', 'rowTransform', false), script('meta', 'metadataProvider')]

describe('the script bindings of a level', () => {
  it('offers only the slots of the level, and only the enabled scripts of each slot', () => {
    const html = renderToStaticMarkup(<ScriptBindingsView bindings={{}} inherited={{}} level="replication" slots={slots} scripts={scripts} onChange={() => {}} />)
    expect(html).toContain('data-testid="script-slot-rowTransform"')
    expect(html).not.toContain('script-slot-metadataProvider')
    expect(html).toContain('>mask-email<')
    expect(html).not.toContain('>old-mask<')
    expect(html).toContain('INHERITED')
    expect(html).toContain('none bound, inherited from the source connection')
  })

  it('names what a broader level binds, and what this level binds or overrides with none', () => {
    const inherited = renderToStaticMarkup(<ScriptBindingsView bindings={{}} inherited={{ rowTransform: { scriptName: 'mask-email', parameters: {} } }} level="mapping" slots={slots} scripts={scripts} onChange={() => {}} />)
    expect(inherited).toContain('Inherit: mask-email')
    const none = renderToStaticMarkup(<ScriptBindingsView bindings={{ rowTransform: null }} inherited={{}} level="mapping" slots={slots} scripts={scripts} onChange={() => {}} />)
    expect(none).toContain('Row transform (none) · overriding the replication or connection')
  })

  it('keeps a bound script the draft does not offer, named as the file says it', () => {
    const html = renderToStaticMarkup(<ScriptBindingsView bindings={{ rowTransform: { scriptName: 'old-mask', parameters: {} } }} inherited={{}} level="mapping" slots={slots} scripts={scripts} onChange={() => {}} />)
    expect(html).toContain('old-mask, not an enabled script of this slot')
  })
})
