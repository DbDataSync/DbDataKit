import type { SecretItem } from './types'

/** A secret's name: a letter, then letters, digits, `.`, `_` or `-`, at most 64 (the Gateway's rule). */
export const SECRET_NAME = /^[A-Za-z][A-Za-z0-9_.-]{0,63}$/

export interface SecretForm { name: string; value: string }

export interface SecretCheck {
  ok: boolean
  /** Whether sending replaces a secret this gateway already keeps under that name. */
  replaces: boolean
  /** Why the button is off, or null when it is on. */
  why: string | null
  /** What is wrong with the name, to show beside it once something is typed. */
  nameProblem: string | null
}

/** What can be told before sending. The value itself is never judged beyond being there: it is a password, and it is not shown. */
export function checkSecret(form: SecretForm, existing: SecretItem[]): SecretCheck {
  const name = form.name.trim()
  const replaces = existing.some((s) => s.name === name)
  const nameProblem = name !== '' && !SECRET_NAME.test(name) ? 'A secret name is a letter followed by letters, digits, \'.\', \'_\' or \'-\' (up to 64).' : null
  if (name === '') return { ok: false, replaces, why: 'Name the secret.', nameProblem }
  if (nameProblem) return { ok: false, replaces, why: nameProblem, nameProblem }
  if (form.value === '') return { ok: false, replaces, why: replaces ? 'Type the new value; the old one is never shown.' : 'Type the value to keep.', nameProblem }
  return { ok: true, replaces, why: null, nameProblem }
}

/** What a data source's file says to use a secret as the database password (the reference is the Gateway's own). */
export const passwordLine = (reference: string) => `password_secret: '${reference}'`

/** The names in order. */
export const sorted = (items: SecretItem[]) => [...items].sort((a, b) => a.name.localeCompare(b.name))

/** The form once a call has been answered: the value is gone whatever the answer was (it is never kept, and a refusal asks for it again); the name stays after a refusal so it can be corrected. */
export const afterCall = (form: SecretForm, ok: boolean): SecretForm => ({ name: ok ? '' : form.name, value: '' })
