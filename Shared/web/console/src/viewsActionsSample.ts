import type { ActionOutcome, ActionRequest, BatchOutcome, BatchRequest, FixtureBundle, RenderedWidget } from '@dbdatakit/visuals'

/**
 * The fixture source's sample of **actions** (plan 32 DBR53): a table of orders whose status is an icon that closes the order, a click on a row that deletes it and an action that asks for a status, with the outcomes a
 * Store gives (waiting for approval, a stale version, a rule refusal, applied) recorded, so the popup, its words and its outcomes can be seen and tried with no server. The Gateway's own dashboards run their actions on the
 * module (`POST .../actions/{link}`); this document and its recorded answers are the fixtures'.
 */
export const ACTIONS_SAMPLE = 'retail-order-actions'

const text = `schemaVersion: 1
kind: dashboard
title: Order actions
links:
  close-order:
    change: {model: orders, entity: orders, edit: {key: $orders.id, version: $orders.version}, set: {status: Closed}}
    confirm: {title: "Close order {{orders.number}}", show: [orders.customer], button: Close order}
  set-status:
    change: {model: orders, entity: orders, edit: {key: $orders.id, version: $orders.version}, set: {status: $ask.status}}
    ask: {status: {label: New status, choices: [Open, Held, Closed]}}
    confirm: {title: "Set order {{orders.number}} to {{ask.status}}", show: [orders.customer]}
  hold-order:
    change: {model: orders, entity: orders, edit: {key: $orders.id, version: $orders.version}, set: {status: Held}}
    confirm: {title: "Hold order {{orders.number}}"}
    stage: true
  drop-order:
    change: {model: orders, entity: orders, delete: {key: $orders.id, version: $orders.version}}
    confirm: {title: "Delete order {{orders.number}}", show: [orders.customer]}
widgets:
  orders:
    type: table
    title: Orders
    encodings:
      columns:
        - {field: orders.id, label: Id}
        - {field: orders.version, label: Version}
        - {field: orders.number, label: Order}
        - {field: orders.customer, label: Customer}
        - {field: orders.status, label: Status, display: icon, icons: {Open: circle, Closed: check}, link: close-order}
        - {field: orders.hold, label: Hold, display: icon, icons: {hold: pause}, link: hold-order}
    query: {model: retail_sales, limit: 100}
    on_click: drop-order
layouts:
  - name: desk
    type: reflow
    default: true
    grid: {columns: 12, rows: 6}
    tiles:
      - {widget: orders, x: 0, y: 0, w: 12, h: 6}
`

const cell = (v: string | number, extra: Record<string, unknown> = {}) => ({ v, text: String(v), ...extra })
const order = (id: number, version: number, number: string, customer: string, status: string) => ({
  cells: [cell(id), cell(version), cell(number), cell(customer), cell(status, { icon: status === 'Closed' ? 'check' : 'circle', link: 'close-order' }), cell('hold', { icon: 'pause', link: 'hold-order' })],
})

const table: RenderedWidget = {
  type: 'table',
  columns: [{ field: 'orders.id', label: 'Id' }, { field: 'orders.version', label: 'Version' }, { field: 'orders.number', label: 'Order' }, { field: 'orders.customer', label: 'Customer' }, { field: 'orders.status', label: 'Status' }, { field: 'orders.hold', label: 'Hold' }],
  rows: [order(1001, 3, 'SO-1001', 'Acme', 'Open'), order(1002, 1, 'SO-1002', 'Initech', 'Open'), order(1003, 5, 'SO-1003', 'Globex', 'Open'), order(1004, 2, 'SO-1004', 'Umbrella', 'Held')],
} as RenderedWidget

const number = (request: ActionRequest) => String(request.row['orders.number'] ?? '')

/** What a Store would answer, by the order: SO-1001 waits for approval, SO-1002's version is stale, SO-1003 breaks a rule, the others apply. */
function answer(request: ActionRequest): ActionOutcome {
  switch (number(request)) {
    case 'SO-1001': return { status: 'waiting', requestId: 'cr-7' }
    case 'SO-1002': return { status: 'conflict', message: 'The row was changed by someone else (it is now version 4): reload and try again.' }
    case 'SO-1003': return { status: 'refused', message: 'The change breaks the model\'s rules, so nothing was written.', violations: [{ rule: 'allowed', attribute: 'status', message: 'An order with an unpaid invoice cannot be closed.' }] }
    default: return { status: 'applied' }
  }
}

/** What a Store would answer to a set: the line of SO-1003 breaks a rule, and none of the set is made; otherwise all of it applies together. */
function batchAnswer(request: BatchRequest): BatchOutcome {
  const bad = request.lines.findIndex((l) => l.row['orders.number'] === 'SO-1003')
  if (bad >= 0) return { status: 'refused', message: 'The changes break the model\'s rules, so none of them was made.', problems: [{ line: bad, rule: 'allowed', attribute: 'status', message: 'An order with an unpaid invoice cannot be held.' }] }
  return { status: 'applied', count: request.lines.length }
}

/** What the console's fixture bundle adds: the document, its recorded table and the answers of its actions. */
export const actionsSample: Required<Pick<FixtureBundle, 'documents' | 'results' | 'actions' | 'batches'>> = {
  documents: { [`dashboard/${ACTIONS_SAMPLE}`]: text },
  results: { [`dashboard/${ACTIONS_SAMPLE}`]: { orders: table } },
  batches: { [`dashboard/${ACTIONS_SAMPLE}`]: { forecast: { will: 'apply' }, outcome: batchAnswer } },
  actions: {
    [`dashboard/${ACTIONS_SAMPLE}`]: {
      'close-order': { forecast: { will: 'wait', note: 'This will wait for approval by the sales owner: it is kept as a change request and applied when it is approved.' }, outcome: answer },
      'set-status': { forecast: { will: 'apply' }, outcome: answer },
      'drop-order': { forecast: { will: 'apply' }, outcome: answer },
    },
  },
}
