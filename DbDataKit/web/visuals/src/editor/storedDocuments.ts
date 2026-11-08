import type { DocumentMap } from '../client/fixtureClient'

/** The part of the browser's `Storage` this uses (so a test passes a fake, and a blocked storage can be replaced by nothing). */
export interface StorageLike {
  getItem(key: string): string | null
  setItem(key: string, value: string): void
}

/**
 * The fixture source's documents kept across page loads (plan 32 DBR24: "save, reload, open: the same"): the bundle's documents underneath, what was saved or deleted on top, in the browser's storage under
 * one key. Every read goes to the storage, so a second tab sees what the first saved (and a stale save in either is a version conflict). A storage that throws or is full degrades to this visit's memory.
 * This is a per-viewer convenience of the fixture source; the real stores (DBR29) keep documents where they belong.
 */
export function storedDocuments(base: Record<string, string>, storage: StorageLike | null, storageKey = 'dbdatakit.views.documents'): DocumentMap {
  let memory: Record<string, string | null> = {}
  const read = (): Record<string, string | null> => {
    try {
      const raw = storage?.getItem(storageKey)
      if (raw) return JSON.parse(raw) as Record<string, string | null>
    } catch { /* unreadable: this visit's memory */ }
    return memory
  }
  const write = (changes: Record<string, string | null>) => {
    memory = changes
    try { storage?.setItem(storageKey, JSON.stringify(changes)) } catch { /* full or blocked: memory */ }
  }
  const all = (): Map<string, string> => {
    const merged = new Map(Object.entries(base))
    for (const [k, v] of Object.entries(read())) { if (v === null) merged.delete(k); else merged.set(k, v) }
    return merged
  }
  return {
    get: (key) => all().get(key),
    set(key, text) { write({ ...read(), [key]: text }) },
    delete(key) { write({ ...read(), [key]: null }) },
    entries: () => all().entries(),
  }
}
