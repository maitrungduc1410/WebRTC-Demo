// Frame encryption compatible with webrtc-sdk FrameCryptor (Android/iOS, M137–M150).
//
// Wire format per encoded frame:
//   [unencrypted header][AES-GCM ciphertext + 16B tag][IV 12B][IV length = 12][key index]
// The header is authenticated as AES-GCM additional data. For H264 everything after the
// header is RBSP-escaped so the payload never contains a start code.
// The AES-128 key is PBKDF2-HMAC-SHA256(material, RATCHET_SALT, 100000).

export const RATCHET_SALT = 'LKFrameEncryptionKey';
export const KEY_MATERIAL_LENGTH = 32;
const KEY_INDEX = 0;
const IV_LENGTH = 12;
const PBKDF2_ITERATIONS = 100000;

export type MediaKind = 'audio' | 'video';
export type VideoCodec = 'vp8' | 'h264' | 'other';
export type CodecMap = Record<number, string>;
type Bytes = Uint8Array<ArrayBuffer>;

export function generateKeyMaterial(): ArrayBuffer {
  return crypto.getRandomValues(new Uint8Array(KEY_MATERIAL_LENGTH)).buffer;
}

/** Key material travels as base64 in the JSON signaling messages. */
export function toBase64(buffer: ArrayBuffer): string {
  return btoa(String.fromCharCode(...new Uint8Array(buffer)));
}

/** Throws on invalid base64. */
export function fromBase64(text: string): ArrayBuffer {
  const binary = atob(text);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes.buffer;
}

export async function deriveFrameKey(material: ArrayBuffer): Promise<CryptoKey> {
  const baseKey = await crypto.subtle.importKey('raw', material, 'PBKDF2', false, ['deriveKey']);
  return crypto.subtle.deriveKey(
    {
      name: 'PBKDF2',
      salt: new TextEncoder().encode(RATCHET_SALT),
      iterations: PBKDF2_ITERATIONS,
      hash: 'SHA-256',
    },
    baseKey,
    { name: 'AES-GCM', length: 128 },
    false,
    ['encrypt', 'decrypt']
  );
}

// payload type -> codec name, from "a=rtpmap:<pt> <codec>/<clock>" SDP lines
export function parseCodecMap(...sdps: (string | undefined)[]): CodecMap {
  const map: CodecMap = {};
  for (const sdp of sdps) {
    if (!sdp) continue;
    for (const match of sdp.matchAll(/^a=rtpmap:(\d+) ([\w-]+)\//gm)) {
      map[Number(match[1])] = match[2].toLowerCase();
    }
  }
  return map;
}

function videoCodecOf(frame: any, codecMap: CodecMap): VideoCodec {
  const metadata = frame.getMetadata?.() ?? {};
  const name: string =
    (metadata.mimeType as string | undefined)?.split('/')[1]?.toLowerCase() ??
    codecMap[metadata.payloadType] ??
    'vp8';
  if (name === 'vp8' || name === 'h264') return name;
  return 'other';
}

function findNaluPayloadOffsets(data: Bytes): number[] {
  const offsets: number[] = [];
  let i = 0;
  while (i + 3 <= data.length) {
    if (data[i] === 0 && data[i + 1] === 0 && data[i + 2] === 1) {
      offsets.push(i + 3);
      i += 3;
    } else {
      i++;
    }
  }
  return offsets;
}

function unencryptedBytes(kind: MediaKind, codec: VideoCodec, data: Bytes, isKeyFrame: boolean): number {
  if (kind === 'audio') return 1;
  if (codec === 'vp8') return isKeyFrame ? 10 : 3;
  if (codec === 'h264') {
    for (const offset of findNaluPayloadOffsets(data)) {
      const naluType = data[offset] & 0x1f;
      if (naluType === 1 || naluType === 5) return offset + 2;
    }
  }
  return 0;
}

function writeRbsp(data: Bytes): Bytes {
  const out: number[] = [];
  let zeros = 0;
  for (const byte of data) {
    if (zeros >= 2 && byte <= 3) {
      out.push(3);
      zeros = 0;
    }
    out.push(byte);
    zeros = byte === 0 ? zeros + 1 : 0;
  }
  return Uint8Array.from(out);
}

function parseRbsp(data: Bytes): Bytes {
  const out: number[] = [];
  let zeros = 0;
  for (const byte of data) {
    if (zeros >= 2 && byte === 3) {
      zeros = 0;
      continue;
    }
    out.push(byte);
    zeros = byte === 0 ? zeros + 1 : 0;
  }
  return Uint8Array.from(out);
}

function needsRbspUnescaping(data: Bytes): boolean {
  for (let i = 0; i + 3 < data.length; i++) {
    if (data[i] === 0 && data[i + 1] === 0 && data[i + 2] === 3) return true;
  }
  return false;
}

const sendCounts = new Map<number, number>();

function makeIv(ssrc: number, timestamp: number): Bytes {
  const sendCount = sendCounts.get(ssrc) ?? Math.floor(Math.random() * 0xffff);
  sendCounts.set(ssrc, sendCount + 1);
  const iv = new DataView(new ArrayBuffer(IV_LENGTH));
  iv.setUint32(0, ssrc >>> 0);
  iv.setUint32(4, timestamp >>> 0);
  iv.setUint32(8, (timestamp - (sendCount % 0xffff)) >>> 0);
  return new Uint8Array(iv.buffer);
}

async function encryptFrame(key: CryptoKey, kind: MediaKind, codec: VideoCodec, frame: any): Promise<boolean> {
  const data = new Uint8Array(frame.data);
  const headerLength = unencryptedBytes(kind, codec, data, frame.type === 'key');
  const header = data.subarray(0, headerLength);
  const metadata = frame.getMetadata?.() ?? {};
  const iv = makeIv(metadata.synchronizationSource ?? 0, frame.timestamp ?? 0);

  const cipherText = new Uint8Array(
    await crypto.subtle.encrypt(
      { name: 'AES-GCM', iv, additionalData: header, tagLength: 128 },
      key,
      data.subarray(headerLength)
    )
  );

  let body = new Uint8Array(cipherText.length + IV_LENGTH + 2);
  body.set(cipherText, 0);
  body.set(iv, cipherText.length);
  body[cipherText.length + IV_LENGTH] = IV_LENGTH;
  body[cipherText.length + IV_LENGTH + 1] = KEY_INDEX;
  if (kind === 'video' && codec === 'h264') body = writeRbsp(body);

  const out = new Uint8Array(headerLength + body.length);
  out.set(header, 0);
  out.set(body, headerLength);
  frame.data = out.buffer;
  return true;
}

async function decryptFrame(key: CryptoKey, kind: MediaKind, codec: VideoCodec, frame: any): Promise<boolean> {
  const data = new Uint8Array(frame.data);
  const headerLength = unencryptedBytes(kind, codec, data, frame.type === 'key');
  const header = data.subarray(0, headerLength);
  let body = data.subarray(headerLength);
  if (kind === 'video' && codec === 'h264' && needsRbspUnescaping(body)) body = parseRbsp(body);
  if (body.length < IV_LENGTH + 2 + 16) return false;

  const ivLength = body[body.length - 2];
  const keyIndex = body[body.length - 1];
  if (ivLength !== IV_LENGTH || keyIndex !== KEY_INDEX) return false;

  const iv = body.subarray(body.length - 2 - IV_LENGTH, body.length - 2);
  const cipherText = body.subarray(0, body.length - 2 - IV_LENGTH);
  const plainText = new Uint8Array(
    await crypto.subtle.decrypt(
      { name: 'AES-GCM', iv, additionalData: header, tagLength: 128 },
      key,
      cipherText
    )
  );

  const out = new Uint8Array(headerLength + plainText.length);
  out.set(header, 0);
  out.set(plainText, headerLength);
  frame.data = out.buffer;
  return true;
}

export interface FrameTransformOptions {
  kind: MediaKind;
  getKey: () => CryptoKey | undefined;
  getCodecMap: () => CodecMap;
}

function createTransform(encrypt: boolean, options: FrameTransformOptions): TransformStream {
  let lastError = '';
  return new TransformStream({
    async transform(frame, controller) {
      // Empty frames (e.g. audio DTX) are forwarded untouched, like the native cryptor does.
      if (frame.data.byteLength === 0) {
        controller.enqueue(frame);
        return;
      }
      // Without a key, drop the frame: never send plaintext, never feed ciphertext to the decoder.
      const key = options.getKey();
      if (!key) return;

      const codec = options.kind === 'video' ? videoCodecOf(frame, options.getCodecMap()) : 'other';
      // A throw inside transform() would error the stream and freeze the track forever.
      try {
        const ok = encrypt
          ? await encryptFrame(key, options.kind, codec, frame)
          : await decryptFrame(key, options.kind, codec, frame);
        if (ok) controller.enqueue(frame);
      } catch (error) {
        const message = `${encrypt ? 'Encrypt' : 'Decrypt'} ${options.kind} frame failed: ${error}`;
        if (message !== lastError) {
          console.warn(message);
          lastError = message;
        }
      }
    },
  });
}

export function encryptStream(options: FrameTransformOptions, readable: ReadableStream, writable: WritableStream) {
  return readable.pipeThrough(createTransform(true, options)).pipeTo(writable);
}

export function decryptStream(options: FrameTransformOptions, readable: ReadableStream, writable: WritableStream) {
  return readable.pipeThrough(createTransform(false, options)).pipeTo(writable);
}