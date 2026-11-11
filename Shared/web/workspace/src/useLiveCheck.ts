import { useEffect, useRef, useState } from 'react'
import { workspaceApi, type CheckDiagnostic } from './api'

const DEBOUNCE_MS = 700

export interface LiveCheck { diagnostics: CheckDiagnostic[]; checkedText: string | null; running: boolean; failed: string | null }

/**
 * The check of a draft as the text changes (plan 33 SC21d, SC22d). A kind's validators read the draft's files, not a text on the screen, so a moment after the last keystroke the text is saved into the draft
 * (`flush`, the editor's own Save: the draft is uncommitted work, nothing is merged or published) and the check runs on it; the answer says which text it was about. `enabled` is false for someone who may not save.
 */
export function useLiveCheck(input: { project: string; id: string; text: string; enabled: boolean; flush: () => Promise<unknown> }): LiveCheck {
  const [state, setState] = useState<LiveCheck>({ diagnostics: [], checkedText: null, running: false, failed: null })
  const ticket = useRef(0)
  const flush = useRef(input.flush)
  flush.current = input.flush
  const { project, id, text, enabled } = input
  useEffect(() => {
    if (!enabled) return
    const mine = ++ticket.current
    const timer = setTimeout(async () => {
      setState((s) => ({ ...s, running: true }))
      try {
        await flush.current()
        const result = await workspaceApi.check(project, id)
        if (mine === ticket.current) setState({ diagnostics: result.diagnostics, checkedText: text, running: false, failed: null })
      } catch (e) {
        if (mine === ticket.current) setState((s) => ({ ...s, running: false, failed: e instanceof Error ? e.message : 'The check failed.' }))
      }
    }, DEBOUNCE_MS)
    return () => clearTimeout(timer)
  }, [project, id, text, enabled])
  return state
}
