<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useStrings } from '../i18n'
import { decryptStream, deriveFrameKey, encryptStream, generateKeyMaterial, toBase64, type MediaKind } from '../../../../web/src/e2ee'

type Sample = 'vp8-key' | 'vp8-delta' | 'h264' | 'opus'
type Part = 'header' | 'cipher' | 'tag' | 'iv' | 'ivlen' | 'index'

/** Made-up but well-formed starts of frames, as an encoder would hand them over. */
const samples: Record<Sample, { kind: MediaKind; type: 'key' | 'delta'; mimeType?: string; header: number[] }> = {
  'vp8-key': { kind: 'video', type: 'key', mimeType: 'video/VP8', header: [0x50, 0x42, 0x00, 0x9d, 0x01, 0x2a, 0x80, 0x02, 0xe0, 0x01] },
  'vp8-delta': { kind: 'video', type: 'delta', mimeType: 'video/VP8', header: [0x31, 0x0a, 0x00] },
  h264: {
    kind: 'video',
    type: 'key',
    mimeType: 'video/H264',
    header: [0, 0, 0, 1, 0x67, 0x42, 0xc0, 0x1f, 0, 0, 0, 1, 0x68, 0xce, 0x3c, 0x80, 0, 0, 0, 1, 0x65, 0x88],
  },
  opus: { kind: 'audio', type: 'key', header: [0x78] },
}

const t = useStrings({
  en: {
    title: 'Encrypt a frame with the web client\'s own code',
    intro: 'This runs encryptStream() and decryptStream() from web/src/e2ee.ts in your browser, the same code the web client runs in its worker.',
    codec: 'Frame',
    payload: 'Frame data after the header',
    material: 'Key material, as sent in "encryption key"',
    newKey: 'New key',
    again: 'Encrypt again',
    wrongKey: 'Receiver has a different key',
    before: 'From the encoder',
    after: 'After encryption, to the packetizer',
    decrypted: 'Receiver decrypted it back to',
    dropped: 'The receiver could not decrypt it, so the frame is dropped. It never reaches the decoder.',
    bytes: 'bytes',
    rbsp: (n: number) => `On the wire the part after the header is RBSP-escaped: ${n} extra 0x03 byte(s) so it never contains a start code. Shown here unescaped.`,
    noCrypto: 'This browser has no Web Crypto here (it needs HTTPS or localhost).',
    parts: {
      header: 'Unencrypted header. The packetizer and decoder still read it. Covered by the GCM tag as additional data.',
      cipher: 'Ciphertext: AES-128-GCM of the rest of the frame, same length as the plain data.',
      tag: 'GCM tag, 16 bytes. If it does not match, the receiver drops the frame.',
      iv: 'IV, 12 bytes: SSRC, RTP timestamp and a send counter. It changes on every frame.',
      ivlen: 'IV length, always 12.',
      index: 'Key index, always 0 here.',
    } as Record<Part, string>,
  },
  vi: {
    title: 'Mã hóa một frame bằng chính code của web client',
    intro: 'Phần này chạy encryptStream() và decryptStream() trong web/src/e2ee.ts ngay trên trình duyệt của bạn, đúng đoạn code web client chạy trong worker.',
    codec: 'Frame',
    payload: 'Dữ liệu frame sau header',
    material: 'Key material, đúng như gửi trong "encryption key"',
    newKey: 'Đổi key',
    again: 'Mã hóa lại',
    wrongKey: 'Bên nhận dùng key khác',
    before: 'Từ encoder ra',
    after: 'Sau khi mã hóa, đưa vào packetizer',
    decrypted: 'Bên nhận giải mã lại thành',
    dropped: 'Bên nhận không giải mã được nên bỏ frame này. Frame không bao giờ tới decoder.',
    bytes: 'byte',
    rbsp: (n: number) => `Khi gửi đi, phần sau header được RBSP-escape: thêm ${n} byte 0x03 để không bao giờ chứa start code. Ở đây hiển thị bản chưa escape.`,
    noCrypto: 'Trình duyệt không có Web Crypto ở đây (cần HTTPS hoặc localhost).',
    parts: {
      header: 'Header không mã hóa. Packetizer và decoder vẫn đọc được. Được GCM tag bảo vệ dưới dạng additional data.',
      cipher: 'Ciphertext: phần còn lại của frame mã hóa bằng AES-128-GCM, dài đúng bằng dữ liệu gốc.',
      tag: 'GCM tag, 16 byte. Không khớp thì bên nhận bỏ frame.',
      iv: 'IV, 12 byte: SSRC, RTP timestamp và bộ đếm số lần gửi. Mỗi frame một IV khác.',
      ivlen: 'Độ dài IV, luôn là 12.',
      index: 'Key index, ở đây luôn là 0.',
    } as Record<Part, string>,
  },
  zh: {
    title: '用 Web 客户端自己的代码加密一帧',
    intro: '这里在你的浏览器中运行 web/src/e2ee.ts 里的 encryptStream() 和 decryptStream()，和 Web 客户端在 worker 里运行的是同一份代码。',
    codec: '帧类型',
    payload: '头部之后的帧数据',
    material: 'Key material，即 "encryption key" 消息里发送的内容',
    newKey: '换一个 key',
    again: '重新加密',
    wrongKey: '接收方使用不同的 key',
    before: '编码器输出',
    after: '加密后，交给打包器',
    decrypted: '接收方解密得到',
    dropped: '接收方无法解密，这一帧被丢弃，不会送到解码器。',
    bytes: '字节',
    rbsp: (n: number) => `实际发送时，头部之后的部分会做 RBSP 转义：多出 ${n} 个 0x03 字节，保证其中不会出现起始码。这里显示的是转义前的内容。`,
    noCrypto: '当前页面无法使用 Web Crypto（需要 HTTPS 或 localhost）。',
    parts: {
      header: '不加密的头部。打包器和解码器仍然可以读取它，并作为附加数据受 GCM tag 保护。',
      cipher: '密文：帧的其余部分经过 AES-128-GCM 加密，长度和明文相同。',
      tag: 'GCM tag，16 字节。校验不通过时，接收方丢弃这一帧。',
      iv: 'IV，12 字节：SSRC、RTP 时间戳和发送计数，每一帧都不同。',
      ivlen: 'IV 长度，固定为 12。',
      index: 'Key index，这里固定为 0。',
    } as Record<Part, string>,
  },
})

const sample = ref<Sample>('vp8-key')
const text = ref('Hello from the encoder')
const wrongKey = ref(false)
const material = ref('')
const before = ref<number[]>([])
const after = ref<number[]>([])
const escapes = ref(0)
/** The codec of the bytes on screen, which lags `sample` until its run finishes. */
const shown = ref<Sample>('vp8-key')
/** undefined until the first run, null when the receiver drops the frame. */
const decrypted = ref<string | null | undefined>(undefined)
const available = ref(true)
const hovered = ref<Part | null>(null)

let senderKey: CryptoKey | undefined
let otherKey: CryptoKey | undefined
let timestamp = 90_000

async function newKey() {
  const bytes = generateKeyMaterial()
  material.value = toBase64(bytes)
  senderKey = await deriveFrameKey(bytes)
  otherKey = await deriveFrameKey(generateKeyMaterial())
  await run()
}

/** Pushes one frame through a transform, as the RTCRtpSender's encoded stream would. */
async function transform(codec: Sample, encrypt: boolean, key: CryptoKey, data: Uint8Array) {
  const current = samples[codec]
  const frame = {
    data: data.slice().buffer,
    type: current.type,
    timestamp,
    getMetadata: () => ({ synchronizationSource: 0x1a2b3c4d, mimeType: current.mimeType }),
  }
  const out: (typeof frame)[] = []
  const readable = new ReadableStream({ start: (controller) => (controller.enqueue(frame), controller.close()) })
  const writable = new WritableStream({ write: (chunk) => void out.push(chunk) })
  const options = { kind: current.kind, getKey: () => key, getCodecMap: () => ({}) }
  await (encrypt ? encryptStream : decryptStream)(options, readable, writable)
  return out[0] ? new Uint8Array(out[0].data) : null
}

function unescape(bytes: Uint8Array) {
  const out: number[] = []
  let zeros = 0
  for (const byte of bytes) {
    if (zeros >= 2 && byte === 3) {
      zeros = 0
      continue
    }
    out.push(byte)
    zeros = byte === 0 ? zeros + 1 : 0
  }
  return out
}

let runs = 0

async function run() {
  if (!senderKey || !otherKey) return
  const id = ++runs
  timestamp += 3000
  const codec = sample.value
  const headerLength = samples[codec].header.length
  const plain = new Uint8Array([...samples[codec].header, ...new TextEncoder().encode(text.value)])
  const encrypted = await transform(codec, true, senderKey, plain)
  const back = encrypted && (await transform(codec, false, wrongKey.value ? otherKey : senderKey, encrypted))
  // Typing or switching codecs starts a newer run; an older one finishing later must not win.
  if (id !== runs || !encrypted) return
  shown.value = codec
  before.value = [...plain]
  if (codec === 'h264') {
    const body = unescape(encrypted.subarray(headerLength))
    escapes.value = encrypted.length - headerLength - body.length
    after.value = [...encrypted.subarray(0, headerLength), ...body]
  } else {
    escapes.value = 0
    after.value = [...encrypted]
  }
  decrypted.value = back ? new TextDecoder().decode(back.subarray(headerLength)) : null
}

function partOf(index: number, length: number): Part {
  const header = samples[shown.value].header.length
  if (index < header) return 'header'
  if (index >= length - 1) return 'index'
  if (index >= length - 2) return 'ivlen'
  if (index >= length - 14) return 'iv'
  if (index >= length - 30) return 'tag'
  return 'cipher'
}

const beforeParts = computed(() => before.value.map((_, i) => (i < samples[shown.value].header.length ? 'header' : 'plain')))
const afterParts = computed(() => after.value.map((_, i) => partOf(i, after.value.length)))
const legend: Part[] = ['header', 'cipher', 'tag', 'iv', 'ivlen', 'index']
const hex = (byte: number) => byte.toString(16).padStart(2, '0')

let pending: ReturnType<typeof setTimeout> | undefined
watch([sample, wrongKey], run)
watch(text, () => {
  clearTimeout(pending)
  pending = setTimeout(run, 250)
})

onMounted(() => {
  if (!globalThis.crypto?.subtle) {
    available.value = false
    return
  }
  void newKey()
})
</script>

<template>
  <div class="demo-card e2ee-frame">
    <h4>{{ t.title }}</h4>
    <p class="demo-muted">{{ t.intro }}</p>
    <p v-if="!available">{{ t.noCrypto }}</p>
    <template v-else>
      <div class="demo-row">
        <span class="field">{{ t.codec }}</span>
        <button
          v-for="name in (['vp8-key', 'vp8-delta', 'h264', 'opus'] as Sample[])"
          :key="name"
          class="demo-button"
          :class="{ active: sample === name }"
          @click="sample = name"
        >
          {{ { 'vp8-key': 'VP8 key frame', 'vp8-delta': 'VP8 delta frame', h264: 'H264', opus: 'Opus' }[name] }}
        </button>
      </div>
      <label class="input">
        <span class="field">{{ t.payload }}</span>
        <input v-model="text" maxlength="60" spellcheck="false" />
      </label>
      <div class="key">
        <span class="field">{{ t.material }}</span>
        <code>{{ material }}</code>
        <div class="demo-row">
          <button class="demo-button" @click="newKey">{{ t.newKey }}</button>
          <button class="demo-button" @click="run">{{ t.again }}</button>
          <label class="toggle"><input v-model="wrongKey" type="checkbox" /> {{ t.wrongKey }}</label>
        </div>
      </div>

      <div class="bytes-title">{{ t.before }} <span class="demo-muted">· {{ before.length }} {{ t.bytes }}</span></div>
      <div class="bytes">
        <span v-for="(byte, i) in before" :key="i" :class="['byte', beforeParts[i]]">{{ hex(byte) }}</span>
      </div>

      <div class="bytes-title">{{ t.after }} <span class="demo-muted">· {{ after.length + escapes }} {{ t.bytes }}</span></div>
      <div class="bytes">
        <span
          v-for="(byte, i) in after"
          :key="i"
          :class="['byte', afterParts[i], { dim: hovered && hovered !== afterParts[i] }]"
          @mouseenter="hovered = afterParts[i]"
          @mouseleave="hovered = null"
        >{{ hex(byte) }}</span>
      </div>
      <p v-if="escapes" class="demo-muted">{{ t.rbsp(escapes) }}</p>

      <ul class="legend">
        <li
          v-for="part in legend"
          :key="part"
          :class="{ active: hovered === part }"
          @mouseenter="hovered = part"
          @mouseleave="hovered = null"
        >
          <span :class="['swatch', part]" />
          <span>{{ t.parts[part] }}</span>
        </li>
      </ul>

      <p v-if="decrypted === undefined" class="result" />
      <p v-else-if="decrypted !== null" class="result ok">{{ t.decrypted }} <code>{{ decrypted }}</code></p>
      <p v-else class="result bad">{{ t.dropped }}</p>
    </template>
  </div>
</template>

<style scoped>
.field {
  margin-right: 4px;
  color: var(--vp-c-text-2);
  font-size: 13px;
}

.input {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  margin-top: 12px;
}

.input input {
  flex: 1;
  min-width: 200px;
  padding: 4px 10px;
  border: 1px solid var(--vp-c-divider);
  border-radius: 8px;
  background: var(--vp-c-bg);
  font-family: var(--vp-font-family-mono);
  font-size: 13px;
}

.key {
  display: flex;
  flex-direction: column;
  gap: 6px;
  margin-top: 12px;
}

.key code {
  align-self: flex-start;
  word-break: break-all;
}

.toggle {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 13px;
}

.bytes-title {
  margin-top: 16px;
  font-size: 13px;
  font-weight: 600;
}

.bytes {
  display: flex;
  flex-wrap: wrap;
  gap: 3px;
  margin-top: 6px;
  font-family: var(--vp-font-family-mono);
  font-size: 12px;
}

.byte {
  padding: 1px 3px;
  border-radius: 4px;
  color: #fff;
  transition: opacity 0.15s;
}

.byte.dim {
  opacity: 0.25;
}

.byte.plain {
  background: var(--vp-c-default-soft);
  color: var(--vp-c-text-1);
}

.header {
  background: var(--demo-header);
}

.cipher {
  background: var(--demo-cipher);
}

.tag {
  background: var(--demo-tag);
}

.iv {
  background: var(--demo-iv);
}

.ivlen,
.index {
  background: var(--demo-trailer);
}

.legend {
  margin: 14px 0 0;
  padding: 0;
  list-style: none;
  font-size: 13px;
}

.legend li {
  display: flex;
  align-items: baseline;
  gap: 8px;
  margin: 4px 0;
  padding: 2px 6px;
  border-radius: 6px;
}

.legend li.active {
  background: var(--vp-c-default-soft);
}

.swatch {
  flex: none;
  width: 12px;
  height: 12px;
  border-radius: 3px;
  transform: translateY(1px);
}

.result {
  margin: 12px 0 0;
  font-size: 14px;
}

.result.ok {
  color: var(--vp-c-green-1);
}

.result.bad {
  color: var(--vp-c-red-1);
}
</style>
