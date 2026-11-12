import type { FromHost, Row, ToHost, ViewProps } from './protocol'
import { checkSetParam } from './protocol'
import type { ParameterDefinition, ParameterValue } from '../views/model'

/**
 * What the host knows of one frame and does with its messages (plan 32 DBR31). It is pure: the frame is reached through `post`, and everything the code can ask for goes through the callbacks, each of which the host
 * answers by the document's rules, not the code's: a parameter must be one the document declares and of its type; a link must be one the widget lists (`links`) and the host resolves its target. A message from a frame
 * that is not in the state it should be (a second `views:ready`, a size before the first `views:hello`) is harmless.
 */
export type FrameState =
  | { kind: 'loading' }
  | { kind: 'ready' }
  | { kind: 'error'; message: string }

export interface FrameHostOptions {
  /** The registered name the frame mounts (the manifest's `export`). */
  name: string
  post(message: FromHost): void
  /** The props now: rows, parameters, theme, size, mode, links. */
  props(): ViewProps
  /** The document's parameters: a view may set only these. */
  definitions: ParameterDefinition[] | undefined
  /** The links the widget lists: a view may open only these. */
  openable: ReadonlySet<string>
  onSetParam(name: string, value: ParameterValue): void
  onOpen(link: string, row: Row): void
  onState(state: FrameState): void
  /** The content's height in CSS pixels, as the frame reports it. */
  onSize?(height: number): void
  /** Something the person should read (a refused parameter or link). */
  onNotice?(message: string): void
}

export interface FrameHost {
  receive(message: ToHost): void
  /** The host's props changed (rows, parameters, size): tell the frame. */
  sendProps(): void
  /** A dialog or panel the view opened closed. */
  sendClosed(link: string, saved: boolean): void
  readonly state: FrameState
  /** The frame announced itself and was given its init. */
  readonly started: boolean
}

export function createFrameHost(o: FrameHostOptions): FrameHost {
  let state: FrameState = { kind: 'loading' }
  let started = false
  const set = (next: FrameState) => {
    // an error is final: a view that failed does not become ready by saying so
    if (state.kind === 'error') return
    state = next
    o.onState(next)
  }
  return {
    get state() { return state },
    get started() { return started },
    receive(message) {
      switch (message.type) {
        case 'views:hello':
          started = true
          o.post({ type: 'views:init', name: o.name, props: o.props() })
          return
        case 'views:ready': if (started) set({ kind: 'ready' }); return
        case 'views:error': set({ kind: 'error', message: message.message }); return
        case 'views:size': o.onSize?.(message.height); return
        case 'views:setParam': {
          const out = checkSetParam(o.definitions, message.name, message.value)
          if (out.ok) o.onSetParam(message.name, out.value)
          else o.onNotice?.(out.message)
          return
        }
        case 'views:open':
          if (!o.openable.has(message.link)) { o.onNotice?.(`The view asked to open \`${message.link}\`, which the widget does not list under \`links\`.`); return }
          o.onOpen(message.link, message.row)
          return
      }
    },
    sendProps() { if (started) o.post({ type: 'views:props', props: o.props() }) },
    sendClosed(link, saved) { if (started) o.post({ type: 'views:closed', link, saved }) },
  }
}
