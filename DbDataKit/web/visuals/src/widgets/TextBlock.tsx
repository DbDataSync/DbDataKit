import type { ParameterValues, TextDoc } from '../views/model'
import { UNSAFE_TEXT_CODE, renderMarkdown, unsafeTextReasons } from './markdown'
import { ErrorTile } from './states'

/** A text widget: its markdown (the safe subset, `markdown.tsx`) with `{{params.name}}` substituted as text. Unsafe text draws as an error tile and nothing of it. */
export function TextBlock({ widget, parameters = {} }: { widget: TextDoc; parameters?: ParameterValues }) {
  const reasons = unsafeTextReasons(widget.text)
  if (reasons.length > 0) return <ErrorTile code={UNSAFE_TEXT_CODE} message={reasons.join(' ')} />
  return <div className="vz-text" data-testid="vz-text">{renderMarkdown(widget.text, parameters)}</div>
}
