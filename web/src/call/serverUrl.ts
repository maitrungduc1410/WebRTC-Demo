/** Port 4000 on the machine that served this page, which is where `npm run dev` puts it. */
export const DEFAULT_SERVER_URL = `http://${location.hostname || 'localhost'}:4000`

const STORAGE_KEY = 'signaling-server'

/**
 * Accepts "192.168.1.10:4000" as well as a full URL and returns "scheme://host[:port]", or null.
 * A path is dropped because Socket.IO would take it for a namespace.
 */
export function normalizeServerUrl(input: string): string | null {
  const text = input.trim()
  if (!text) return null
  const withScheme = /^[a-z][a-z\d+.-]*:\/\//i.test(text) ? text : `http://${text}`
  let url: URL
  try {
    url = new URL(withScheme)
  } catch {
    return null
  }
  if ((url.protocol !== 'http:' && url.protocol !== 'https:') || !url.hostname) return null
  return url.origin
}

export function loadServerUrl(): string {
  try {
    return normalizeServerUrl(localStorage.getItem(STORAGE_KEY) ?? '') ?? DEFAULT_SERVER_URL
  } catch {
    return DEFAULT_SERVER_URL
  }
}

export function saveServerUrl(url: string) {
  try {
    // Nothing stored means "the default", which follows the host the page is opened from.
    if (url === DEFAULT_SERVER_URL) localStorage.removeItem(STORAGE_KEY)
    else localStorage.setItem(STORAGE_KEY, url)
  } catch {
    // Storage can be unavailable (private mode); the address still applies to this visit.
  }
}
