import type { ImageDoc } from '../views/model'
import type { RenderedImage } from '../views/rendered'
import { ErrorTile } from './states'

/**
 * An image widget (plan 32 section 2): a raster held in the definition's artifacts **by hash**, never fetched from a URL, never an SVG (it can script). The client resolves the hash to a `data:` URI of the
 * raster; the renderer draws only that, and a `src` of any other kind (an address, a `javascript:` or `blob:` link, an SVG, a type other than the widget says) is an error tile.
 * The **size cap** is checked where the bytes enter the definition: `checkImageBytes`, which a publish calls (a raster by its first bytes, never by its name).
 */
export const IMAGE_MAX_BYTES = 1_048_576
export const UNSAFE_IMAGE_CODE = 'DBD-VIEW-003'

const SIGNATURES: Record<ImageDoc['media_type'], (b: Uint8Array) => boolean> = {
  'image/png': (b) => [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a].every((v, i) => b[i] === v),
  'image/jpeg': (b) => b[0] === 0xff && b[1] === 0xd8 && b[2] === 0xff,
  'image/webp': (b) => [0x52, 0x49, 0x46, 0x46].every((v, i) => b[i] === v) && [0x57, 0x45, 0x42, 0x50].every((v, i) => b[8 + i] === v),
}

export type ImageCheck = { ok: true } | { ok: false; code: string; message: string }

/** Whether bytes may be held as an image widget's picture: a png, jpeg or webp by its first bytes (so an SVG or a renamed file is refused whatever it is called), of at most `IMAGE_MAX_BYTES`. */
export function checkImageBytes(bytes: Uint8Array, mediaType: string): ImageCheck {
  if (!(mediaType in SIGNATURES)) return { ok: false, code: UNSAFE_IMAGE_CODE, message: `\`${mediaType}\` is not an image type a widget holds: png, jpeg or webp (never SVG, which can script).` }
  if (bytes.length > IMAGE_MAX_BYTES) return { ok: false, code: UNSAFE_IMAGE_CODE, message: `The image is ${bytes.length} bytes; a widget's image is at most ${IMAGE_MAX_BYTES}.` }
  if (!SIGNATURES[mediaType as ImageDoc['media_type']](bytes)) return { ok: false, code: UNSAFE_IMAGE_CODE, message: `The bytes are not a ${mediaType}.` }
  return { ok: true }
}

/** Whether a `src` may be drawn for an image widget of this media type: a base64 `data:` URI of exactly that raster type, within the size cap, and nothing else. */
export function isSafeImageSrc(src: string, mediaType: string): boolean {
  const prefix = `data:${mediaType};base64,`
  if (!(mediaType in SIGNATURES) || !src.startsWith(prefix)) return false
  const body = src.slice(prefix.length)
  return body.length <= Math.ceil((IMAGE_MAX_BYTES * 4) / 3) + 4 && /^[A-Za-z0-9+/]*={0,2}$/.test(body)
}

export function ImageBlock({ widget, result }: { widget: ImageDoc; result: RenderedImage }) {
  if (!isSafeImageSrc(result.src, widget.media_type))
    return <ErrorTile code={UNSAFE_IMAGE_CODE} message={`The image is not a ${widget.media_type} held by hash: a widget draws no address, no SVG and no other type.`} />
  return <img className={`vz-image vz-fit-${widget.fit ?? 'contain'}`} data-testid="vz-image" src={result.src} alt={widget.alt} />
}
