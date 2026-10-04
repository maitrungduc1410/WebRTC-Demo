import {
  decryptStream,
  deriveFrameKey,
  encryptStream,
  generateKeyMaterial,
  parseCodecMap,
  type CodecMap,
  type MediaKind,
} from '@/e2ee'

const USE_ENCRYPTION_WORKER = true

/**
 * The E2EE side of one call: the shared frame key (in the worker, or on the main thread without
 * it) and the encoded transforms on senders and receivers. Used by the 1:1 and the group engine.
 */
export function createFrameCrypto() {
  let encryptionKey: CryptoKey | undefined
  let codecMap: CodecMap = {}
  let encryptionWorker: Worker | undefined
  let keyRequests: ((material: ArrayBuffer) => void)[] = []
  /** Bumped by stop(), so a key derived for an ended call is never used by the next one. */
  let session = 0
  const transformedRtpObjects = new WeakSet<RTCRtpSender | RTCRtpReceiver>()

  function start() {
    if (!USE_ENCRYPTION_WORKER || encryptionWorker) return
    const worker = new Worker(new URL('../encryptionWorker.ts', import.meta.url), { type: 'module' })
    encryptionWorker = worker
    worker.onmessage = event => {
      if (worker !== encryptionWorker) return
      const { action, key } = event.data
      if (action === 'generatedKey') keyRequests.shift()?.(key)
    }
  }

  function stop() {
    encryptionWorker?.terminate()
    encryptionWorker = undefined
    encryptionKey = undefined
    codecMap = {}
    keyRequests = []
    session++
  }

  /** Fresh key material, already used for our frames; null when the call ended meanwhile. */
  async function generateKey(): Promise<ArrayBuffer | null> {
    const current = session
    const worker = encryptionWorker
    if (worker) {
      const material = await new Promise<ArrayBuffer>(resolve => {
        keyRequests.push(resolve)
        worker.postMessage({ action: 'generateKey' })
      })
      return current === session ? material : null
    }
    const material = generateKeyMaterial()
    const key = await deriveFrameKey(material)
    if (current !== session) return null
    encryptionKey = key
    return material
  }

  async function setKey(material: ArrayBuffer) {
    if (encryptionWorker) {
      encryptionWorker.postMessage({ action: 'setKey', key: material })
      return
    }
    const current = session
    const key = await deriveFrameKey(material)
    if (current === session) encryptionKey = key
  }

  /** Payload type → codec, so the transform knows the VP8/H264 header size of each frame. */
  function updateCodecMap(...sdps: (string | undefined)[]) {
    codecMap = parseCodecMap(...sdps)
    encryptionWorker?.postMessage({ action: 'setCodecMap', codecMap })
  }

  function attach(senderOrReceiver: RTCRtpSender | RTCRtpReceiver, operation: 'encrypt' | 'decrypt', kind: MediaKind) {
    if (transformedRtpObjects.has(senderOrReceiver)) return

    const target = senderOrReceiver as any
    const transformOptions = { kind, getKey: () => encryptionKey, getCodecMap: () => codecMap }

    if (typeof target.createEncodedStreams === 'function') {
      const { readable, writable } = target.createEncodedStreams()
      transformedRtpObjects.add(senderOrReceiver)
      if (USE_ENCRYPTION_WORKER && encryptionWorker) {
        encryptionWorker.postMessage({ action: operation, kind, readable, writable }, [readable, writable])
      } else {
        const pipe = operation === 'encrypt' ? encryptStream : decryptStream
        pipe(transformOptions, readable, writable).catch(error => console.error('E2EE pipeline closed:', error))
      }
    } else if ('RTCRtpScriptTransform' in window && encryptionWorker) {
      // @ts-ignore
      target.transform = new RTCRtpScriptTransform(encryptionWorker, { operation, kind })
      transformedRtpObjects.add(senderOrReceiver)
    } else {
      console.error('E2EE is not supported in this browser')
    }
  }

  return { start, stop, generateKey, setKey, updateCodecMap, attach }
}

export type FrameCrypto = ReturnType<typeof createFrameCrypto>
