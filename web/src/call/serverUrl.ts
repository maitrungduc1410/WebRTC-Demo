/** Port 4000 on the machine that served this page, which is where `npm run dev` puts it. */
export const DEFAULT_SERVER_URL = `http://${location.hostname || 'localhost'}:4000`

const STORAGE_KEY = 'signaling-server'

const PROBE_TIMEOUT_MS = 3000

/**
 * Accepts "192.168.1.10:4000" as well as a full URL and returns "scheme://host[:port]", or null.
 * A path is dropped: both servers take their WebSocket on /ws.
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
  return load(STORAGE_KEY, normalizeServerUrl, DEFAULT_SERVER_URL)
}

export function saveServerUrl(url: string) {
  save(STORAGE_KEY, url, DEFAULT_SERVER_URL)
}

/** "http://host:4000" → "ws://host:4000/ws"; both servers signal on /ws. */
export function webSocketUrl(url: string): string {
  return `${url.replace(/^http/, 'ws')}/ws`
}

/** GET / of either server answers {"name": ..., "ok": true}; only used for the lobby's status dot. */
export async function probeServer(url: string, name: 'signaling-server' | 'sfu-server'): Promise<boolean> {
  try {
    const response = await fetch(`${url}/`, { cache: 'no-store', signal: AbortSignal.timeout(PROBE_TIMEOUT_MS) })
    return (await response.json())?.name === name
  } catch {
    return false
  }
}

// ---- Group call (SFU) server ------------------------------------------------------------------

/** The SFU does signaling (WebSocket on /ws) and media on port 4001. */
export const DEFAULT_SFU_URL = `http://${location.hostname || 'localhost'}:4001`

const SFU_STORAGE_KEY = 'sfu-server'

/** Like normalizeServerUrl, and also takes the WebSocket form "ws://host:4001/ws". */
export function normalizeSfuUrl(input: string): string | null {
  return normalizeServerUrl(input.trim().replace(/^ws(s?):\/\//i, 'http$1://'))
}

export function loadSfuUrl(): string {
  return load(SFU_STORAGE_KEY, normalizeSfuUrl, DEFAULT_SFU_URL)
}

export function saveSfuUrl(url: string) {
  save(SFU_STORAGE_KEY, url, DEFAULT_SFU_URL)
}

function load(key: string, normalize: (input: string) => string | null, fallback: string): string {
  try {
    return normalize(localStorage.getItem(key) ?? '') ?? fallback
  } catch {
    return fallback
  }
}

function save(key: string, url: string, fallback: string) {
  try {
    // Nothing stored means "the default", which follows the host the page is opened from.
    if (url === fallback) localStorage.removeItem(key)
    else localStorage.setItem(key, url)
  } catch {
    // Storage can be unavailable (private mode); the address still applies to this visit.
  }
}
