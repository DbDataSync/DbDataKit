// The same app as ../territory-notes, as a source tree: TypeScript that `dbdatagateway apps build` bundles into main.js.
// A sandboxed document has an opaque origin: no cookies, no localStorage, no direct line to the host's pages.
// It talks to the host's shell page over postMessage ({dbdata: 'app/1'}): a token for data calls, and storage.
// Every outside address is refused by the page's Content-Security-Policy; the data route is the host's own.
const $ = (id: string) => document.getElementById(id) as HTMLInputElement
type Reply = { dbdata: string; id: string; type: string; message?: string; [key: string]: any }
const waiting = new Map<string, (m: Reply) => void>()
window.addEventListener('message', (event) => {
  const m = event.data
  if (event.source !== window.parent || !m || m.dbdata !== 'app/1' || !waiting.has(m.id)) return
  waiting.get(m.id)!(m)
  waiting.delete(m.id)
})
const ask = (message: object, ok: string) => new Promise<Reply>((resolve, reject) => {
  const id = String(Math.random())
  waiting.set(id, (m) => (m.type === ok ? resolve(m) : reject(new Error(m.message || m.type))))
  window.parent.postMessage({ dbdata: 'app/1', id, ...message }, '*')
})
const token = async () => (await ask({ type: 'token.request' }, 'token')).token
const store = async (body: object) => (await ask({ type: 'storage', ...body }, 'storage.result')).result

// the instance's own address, taken from where this document was served: /apps/<instance>/_app/
const instance = location.pathname.split('/')[2]
const data = `/apps/${instance}/_data/api/v1/models/territories`

async function load() {
  const headers = { Authorization: 'Bearer ' + (await token()) }
  const response = await fetch(`${data}/entities/territory/rows`, { headers })
  if (!response.ok) throw new Error('reading the territories: ' + response.status)
  const body = $('rows').querySelector('tbody')
  body.replaceChildren(...(await response.json()).rows.map((r) => {
    const tr = document.createElement('tr')
    for (const value of [r.values.code, r.values.name, r.values.quota ?? '']) tr.append(Object.assign(document.createElement('td'), { textContent: value }))
    return tr
  }))
  $('status').textContent = 'Signed in through the host; the rows come from the Store model "territories".'
}

$('add').addEventListener('submit', async (event) => {
  event.preventDefault()
  try {
    const headers = { Authorization: 'Bearer ' + (await token()), 'Content-Type': 'application/json' }
    const values: Record<string, unknown> = { code: $('code').value, name: $('name').value }
    if ($('quota').value) values.quota = Number($('quota').value)
    const response = await fetch(`${data}/changes`, { method: 'POST', headers, body: JSON.stringify({ changes: [{ action: 'insert', entity: 'territory', values }] }) })
    $('status').textContent = response.ok ? `Added ${values.code}.` : `The model refused it (${response.status}): ${await response.text()}`
    if (response.ok) await load()
  } catch (e) { $('status').textContent = 'Failed: ' + (e as Error).message }
})

$('save-note').addEventListener('click', async () => {
  try {
    await store({ op: 'set', scope: 'user', key: 'note', value: $('note').value })
    $('saved').textContent = 'Saved in the app storage (user scope).'
  } catch (e) { $('saved').textContent = 'Failed: ' + (e as Error).message }
})

load().catch((e: Error) => { $('status').textContent = 'Failed: ' + e.message })
store({ op: 'get', scope: 'user', key: 'note' }).then((r) => { if (r.value != null) $('note').value = r.value }, () => {})
