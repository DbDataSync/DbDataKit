import { createContext, useContext, useEffect, useState, type ReactNode } from 'react'
import type { StoreClient } from './client'

const Context = createContext<StoreClient | null>(null)

/** Gives the controls below it the Store they talk to. A host (the console, an app) puts one around whatever it renders. */
export function StoreClientProvider({ client, children }: { client: StoreClient; children: ReactNode }) {
  return <Context.Provider value={client}>{children}</Context.Provider>
}

export function useStoreClient(): StoreClient {
  const client = useContext(Context)
  if (!client) throw new Error('A Store control needs a StoreClientProvider above it.')
  return client
}

export interface Loaded<T> { data: T | undefined; error: Error | null; loading: boolean; reload: () => void }

/** Runs a read when its inputs change; the latest answer wins. Small on purpose: a host with a query cache can use the views and the client directly. */
export function useLoad<T>(load: () => Promise<T>, deps: readonly unknown[]): Loaded<T> {
  const [state, setState] = useState<{ data: T | undefined; error: Error | null; loading: boolean }>({ data: undefined, error: null, loading: true })
  const [tick, setTick] = useState(0)
  useEffect(() => {
    let current = true
    setState((s) => ({ ...s, loading: true }))
    load().then(
      (data) => { if (current) setState({ data, error: null, loading: false }) },
      (error: unknown) => { if (current) setState({ data: undefined, error: error instanceof Error ? error : new Error(String(error)), loading: false }) },
    )
    return () => { current = false }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...deps, tick])
  return { ...state, reload: () => setTick((t) => t + 1) }
}
