import { useState } from 'react'
import type { AdoptionRequest, PublishOptions, PublishTarget } from '../api'
import { adoptionSentence, parseBindings, productWord, publishButton } from '../model'

/**
 * The publish form of a build, by the product of the target it names (plan 33 SC20d, SC21b, SC22c): any target takes a note; a Runner takes the channel the package is staged on (and, if asked, serves it at once, and the slots to bind there,
 * `slot=connection`); a Gateway that takes models takes the data source they are on (blank: the model's own storage connection). A target the project's kind is not published to is not offered (the server says which with `accepts`).
 * When the Gateway refuses a model for a data source made by hand, `adoption` (read from its refusal) shows a confirmation that names the data source and who made it (plan 33 SC21f); **Confirm and publish** publishes again with `adopt`, which the Gateway records.
 * It sends nothing itself: `onPublish` gets the target, the note and the options, so the screen and its tests say the same.
 */
export function PublishForm({ canWrite, status, targets, models = false, busy, onPublish, initialTarget = '', adoption = null, onCancelAdoption }: {
  canWrite: boolean; status: string; targets: readonly PublishTarget[]; models?: boolean; busy: boolean
  onPublish: (target: string, note: string, options: PublishOptions) => void; initialTarget?: string
  adoption?: AdoptionRequest | null; onCancelAdoption?: () => void
}) {
  const [adopted, setAdopted] = useState<string[]>([])
  const [target, setTarget] = useState(initialTarget)
  const [note, setNote] = useState('')
  const [channel, setChannel] = useState('')
  const [serve, setServe] = useState(false)
  const [bindText, setBindText] = useState('')
  const [dataSource, setDataSource] = useState('')
  const offered = targets.filter((t) => t.accepts !== false)
  const known = targets.find((t) => t.name === target.trim())
  const bindings = parseBindings(bindText)
  const state = publishButton({ canWrite, status, target, targets, busy, channel })
  const why = bindings.problem ?? state.why
  const enabled = state.enabled && bindings.problem === null
  const send = (adopt: string[] = adopted) => {
    const options: PublishOptions = {}
    if (known?.product === 'runner') {
      options.channel = channel.trim()
      if (serve) options.serve = true
      if (Object.keys(bindings.bind).length > 0) options.bind = bindings.bind
    } else if (models && dataSource.trim() !== '') options.data_source = dataSource.trim()
    if (known?.product === 'gateway' && adopt.length > 0) options.adopt = adopt
    onPublish(target.trim(), note.trim(), options)
  }
  const confirm = () => {
    if (!adoption) return
    const next = [...new Set([...adopted, adoption.data_source])]
    setAdopted(next)
    send(next)
  }
  return (
    <form className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap', marginTop: 10 }} data-testid="wsp-publish-form" data-product={known?.product ?? ''} onSubmit={(e) => { e.preventDefault(); if (enabled) send() }}>
      <input className="input mono" list="wsp-targets" aria-label="Publish to" placeholder="publisher key" value={target} onChange={(e) => setTarget(e.target.value)} data-testid="wsp-publish-target" />
      <datalist id="wsp-targets">{offered.map((t) => <option key={t.name} value={t.name}>{productWord(t.product)} {t.gateway ?? 'not paired'}</option>)}</datalist>
      {known?.product === 'runner' && (
        <>
          <input className="input mono" style={{ width: 130 }} aria-label="Channel" placeholder="channel" value={channel} onChange={(e) => setChannel(e.target.value)} data-testid="wsp-publish-channel" />
          <input className="input mono" style={{ minWidth: 220 }} aria-label="Connections to bind" placeholder="bind slot=connection, ... (optional)" value={bindText} onChange={(e) => setBindText(e.target.value)} data-testid="wsp-publish-bind" />
          <label className="hint" data-testid="wsp-publish-serve-label"><input type="checkbox" checked={serve} onChange={(e) => setServe(e.target.checked)} data-testid="wsp-publish-serve" /> make it active at once (otherwise it is staged and the Runners activate it when every one has it ready)</label>
        </>
      )}
      {known?.product === 'gateway' && models && (
        <input className="input mono" style={{ minWidth: 200 }} aria-label="Data source" placeholder="data source (blank: the model's storage connection)" value={dataSource} onChange={(e) => setDataSource(e.target.value)} data-testid="wsp-publish-datasource" />
      )}
      <input className="input" style={{ minWidth: 200 }} aria-label="Note" placeholder="note for the version (optional)" value={note} onChange={(e) => setNote(e.target.value)} data-testid="wsp-publish-note" />
      <button type="submit" className="btn btn-primary" disabled={!enabled} title={why ?? undefined} data-testid="wsp-publish">Publish</button>
      {why && <span className="hint" data-testid="wsp-publish-why">{why}</span>}
      {adoption && !busy && (
        <div className="callout" style={{ flexBasis: '100%' }} role="alertdialog" aria-label={`Adopt the data source ${adoption.data_source}`} data-testid="wsp-publish-adopt" data-data-source={adoption.data_source} data-made-by={adoption.made_by}>
          <div data-testid="wsp-publish-adopt-text">{adoptionSentence(adoption)}</div>
          <div className="row" style={{ gap: 8, marginTop: 8 }}>
            <button type="button" className="btn btn-primary" onClick={confirm} data-testid="wsp-publish-adopt-confirm">Confirm and publish onto {adoption.data_source}</button>
            <button type="button" className="btn" onClick={() => onCancelAdoption?.()} data-testid="wsp-publish-adopt-cancel">Do not publish</button>
          </div>
        </div>
      )}
    </form>
  )
}
