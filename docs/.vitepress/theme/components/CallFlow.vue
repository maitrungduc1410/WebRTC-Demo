<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useLocale, useStrings, type Text } from '../i18n'

type Lane = 'A' | 'S' | 'B'

interface Step {
  from: Lane
  to: Lane
  /** Relayed by the server on its way from `from` to `to`. */
  relayed?: boolean
  /** Sent both ways at once. */
  both?: boolean
  /** Peer to peer, not through the server. */
  direct?: boolean
  e2eeOnly?: boolean
  label: string
  payload?: object
  text: Text
}

const sdp = 'v=0\r\no=- 4611731400430051336 2 IN IP4 127.0.0.1\r\n… (a few KB)'

const allSteps: Step[] = [
  {
    from: 'A',
    to: 'S',
    label: 'join',
    payload: { type: 'join', roomId: 'demo' },
    text: {
      en: 'Peer A opens a WebSocket to the signaling server and joins room "demo". The room is empty, so the server creates it and A waits.',
      vi: 'Peer A mở WebSocket tới signaling server và vào phòng "demo". Phòng đang trống nên server tạo phòng mới, A ngồi chờ.',
      zh: '对端 A 打开到信令服务器的 WebSocket，加入房间 "demo"。房间是空的，服务器创建房间，A 开始等待。',
    },
  },
  {
    from: 'B',
    to: 'S',
    label: 'join',
    payload: { type: 'join', roomId: 'demo' },
    text: {
      en: 'Peer B joins the same room. A third peer would get a fatal "Room is full" error.',
      vi: 'Peer B vào cùng phòng. Nếu có người thứ ba vào, họ sẽ nhận lỗi fatal "Room is full".',
      zh: '对端 B 加入同一个房间。如果还有第三个人加入，会收到致命错误 "Room is full"。',
    },
  },
  {
    from: 'S',
    to: 'A',
    label: 'peer joined',
    payload: { type: 'peer joined' },
    text: {
      en: 'The server tells the peer that was already waiting. That peer always makes the offer, so the two sides never offer at the same time.',
      vi: 'Server báo cho peer đang chờ sẵn. Peer này luôn là bên tạo offer, nhờ vậy hai bên không bao giờ cùng offer một lúc.',
      zh: '服务器通知已经在等待的一方。总是由这一方发起 offer，所以双方永远不会同时发 offer。',
    },
  },
  {
    from: 'A',
    to: 'B',
    relayed: true,
    e2eeOnly: true,
    label: 'encryption key',
    payload: { type: 'encryption key', key: 'q8J0yV0b3m2Hk3c4Q2l5N1Zp5f0c2xg9u7XrM4Wv0aE=' },
    text: {
      en: 'With E2EE on, A generates 32 random bytes and sends them before the offer. A WebSocket keeps order, so B always has the key before the first encrypted frame. Each side turns the bytes into the same AES key with PBKDF2.',
      vi: 'Khi bật E2EE, A tạo 32 byte ngẫu nhiên và gửi đi trước offer. WebSocket giữ đúng thứ tự, nên B luôn có key trước khi frame mã hóa đầu tiên tới. Mỗi bên tự tạo cùng một AES key từ 32 byte đó bằng PBKDF2.',
      zh: '开启 E2EE 时，A 生成 32 个随机字节，并在 offer 之前发出。WebSocket 保证顺序，所以 B 总是在第一个加密帧到达前拿到 key。双方各自用 PBKDF2 从这些字节派生出相同的 AES key。',
    },
  },
  {
    from: 'B',
    to: 'A',
    relayed: true,
    e2eeOnly: true,
    label: 'encryption key received',
    payload: { type: 'encryption key received' },
    text: {
      en: 'B acknowledges the key. The clients only log it.',
      vi: 'B xác nhận đã nhận key. Các client chỉ ghi log message này.',
      zh: 'B 确认收到 key。客户端只会把它记到日志里。',
    },
  },
  {
    from: 'A',
    to: 'B',
    relayed: true,
    label: 'offer',
    payload: { type: 'offer', sdp },
    text: {
      en: 'A creates its peer connection, adds the microphone and camera tracks and the chat data channel, puts VP8 first when E2EE is on, and sends the offer. The server relays it without reading it.',
      vi: 'A tạo peer connection, thêm track mic, track camera và data channel cho chat, đưa VP8 lên đầu khi bật E2EE, rồi gửi offer. Server chuyển tiếp nguyên vẹn, không đọc nội dung.',
      zh: 'A 创建 peer connection，添加麦克风和摄像头 track 以及聊天用的 data channel，开启 E2EE 时把 VP8 放在首位，然后发送 offer。服务器原样转发，不解析内容。',
    },
  },
  {
    from: 'B',
    to: 'A',
    relayed: true,
    label: 'answer',
    payload: { type: 'answer', sdp },
    text: {
      en: 'B sets the offer as its remote description, adds its own tracks and answers.',
      vi: 'B đặt offer làm remote description, thêm track của mình rồi gửi answer.',
      zh: 'B 把 offer 设为 remote description，添加自己的 track，然后回复 answer。',
    },
  },
  {
    from: 'A',
    to: 'B',
    relayed: true,
    both: true,
    label: 'candidate',
    payload: {
      type: 'candidate',
      candidate: {
        candidate: 'candidate:842163049 1 udp 1677729535 203.0.113.7 54321 typ srflx raddr 192.168.1.10 rport 54321',
        sdpMid: '0',
        sdpMLineIndex: 0,
      },
    },
    text: {
      en: 'Both sides send ICE candidates as they find them (trickle ICE). The public addresses come from Google\'s STUN server.',
      vi: 'Hai bên gửi ICE candidate ngay khi tìm được (trickle ICE). Địa chỉ public lấy từ STUN server của Google.',
      zh: '双方一边收集一边发送 ICE candidate（trickle ICE）。公网地址来自 Google 的 STUN 服务器。',
    },
  },
  {
    from: 'A',
    to: 'B',
    direct: true,
    both: true,
    label: 'DTLS-SRTP + SCTP',
    text: {
      en: 'ICE and DTLS connect. Audio, video and chat now go straight between the two peers. The server is out of the media path.',
      vi: 'ICE và DTLS kết nối xong. Từ giờ audio, video và chat đi thẳng giữa hai peer, không qua server nữa.',
      zh: 'ICE 和 DTLS 连接成功。音频、视频和聊天从此直接在双方之间传输，不再经过服务器。',
    },
  },
  {
    from: 'A',
    to: 'B',
    relayed: true,
    both: true,
    label: 'media state',
    payload: { type: 'media state', state: { audio: true, video: true, screen: false } },
    text: {
      en: 'Each side sends its mic, camera and sharing state once the peers connect, then again on every change. A disabled track still sends black frames, so this is how the other side knows the camera is off.',
      vi: 'Khi đã kết nối, mỗi bên gửi trạng thái mic, camera và chia sẻ màn hình, sau đó gửi lại mỗi khi có thay đổi. Track bị tắt vẫn gửi frame đen, nên bên kia chỉ biết camera đã tắt nhờ message này.',
      zh: '连接后双方各自发送麦克风、摄像头和共享状态，之后每次变化都会再发一次。被禁用的 track 仍会发送黑帧，对端只能靠这条消息知道摄像头关了。',
    },
  },
  {
    from: 'B',
    to: 'S',
    label: 'leave',
    payload: { type: 'leave' },
    text: {
      en: 'B hangs up: it sends leave and closes its socket, and the server frees the seat. The server tells A nothing. A sees the data channel close (or the connection drop) and goes back to waiting.',
      vi: 'B gác máy: gửi leave rồi đóng socket, server giải phóng chỗ trong phòng. Server không báo gì cho A. A tự biết khi data channel đóng (hoặc kết nối rớt) và quay về trạng thái chờ.',
      zh: 'B 挂断：发送 leave 并关闭 socket，服务器释放这个位置。服务器不会通知 A。A 发现 data channel 关闭（或连接断开）后回到等待状态。',
    },
  },
]

const t = useStrings({
  en: { title: 'Step through a 1:1 call', back: 'Back', next: 'Next', restart: 'Start over', e2ee: 'E2EE on', step: 'Step', of: 'of', server: 'Signaling server', direct: 'peer to peer' },
  vi: { title: 'Xem từng bước một cuộc gọi 1:1', back: 'Lùi', next: 'Tiếp', restart: 'Làm lại', e2ee: 'Bật E2EE', step: 'Bước', of: '/', server: 'Signaling server', direct: 'peer to peer' },
  zh: { title: '逐步查看一次 1:1 通话', back: '上一步', next: '下一步', restart: '重新开始', e2ee: '开启 E2EE', step: '第', of: '/', server: '信令服务器', direct: '点对点' },
})
const locale = useLocale()

const e2ee = ref(true)
const steps = computed(() => allSteps.filter((step) => e2ee.value || !step.e2eeOnly))
const current = ref(0)
watch(e2ee, () => (current.value = 0))

const shown = computed(() => steps.value.slice(0, current.value + 1))
const step = computed(() => steps.value[current.value])
const lanes: Lane[] = ['A', 'S', 'B']
const position: Record<Lane, number> = { A: 1, S: 3, B: 5 }

function arrowStyle(item: Step) {
  const from = position[item.from]
  const to = position[item.to]
  const left = (Math.min(from, to) / 6) * 100
  const width = (Math.abs(to - from) / 6) * 100
  return { left: `${left}%`, width: `${width}%` }
}

function direction(item: Step) {
  if (item.both) return 'both'
  return position[item.to] > position[item.from] ? 'right' : 'left'
}

function laneName(lane: Lane) {
  return lane === 'S' ? t.value.server : `Peer ${lane}`
}
</script>

<template>
  <div class="demo-card call-flow">
    <h4>{{ t.title }}</h4>
    <div class="lanes">
      <div v-for="lane in lanes" :key="lane" class="lane-head" :class="{ server: lane === 'S' }">{{ laneName(lane) }}</div>
    </div>
    <div class="sequence">
      <div class="lifelines">
        <span v-for="lane in lanes" :key="lane" :style="{ left: `${(position[lane] / 6) * 100}%` }" />
      </div>
      <div
        v-for="(item, index) in shown"
        :key="`${e2ee}-${index}`"
        class="row"
        :class="{ current: index === current }"
        @click="current = index"
      >
        <div class="arrow" :class="[direction(item), { direct: item.direct, relayed: item.relayed }]" :style="arrowStyle(item)">
          <span v-if="item.relayed" class="hop" />
          <span class="label">{{ item.label }}<template v-if="item.direct"> · {{ t.direct }}</template></span>
        </div>
      </div>
    </div>
    <div class="detail">
      <p :key="`${locale}-${current}`" class="explain">{{ step.text[locale] }}</p>
      <pre v-if="step.payload"><code>{{ JSON.stringify(step.payload, null, 2) }}</code></pre>
    </div>
    <div class="demo-row controls">
      <button class="demo-button" :disabled="current === 0" @click="current--">{{ t.back }}</button>
      <button class="demo-button active" :disabled="current === steps.length - 1" @click="current++">{{ t.next }}</button>
      <button class="demo-button" @click="current = 0">{{ t.restart }}</button>
      <label class="toggle"><input v-model="e2ee" type="checkbox" /> {{ t.e2ee }}</label>
      <span class="demo-muted counter">{{ t.step }} {{ current + 1 }} {{ t.of }} {{ steps.length }}</span>
    </div>
  </div>
</template>

<style scoped>
.lanes {
  display: grid;
  grid-template-columns: repeat(3, 1fr);
  text-align: center;
  font-size: 13px;
  font-weight: 600;
}

.lane-head {
  justify-self: center;
  padding: 4px 10px;
  border-radius: 8px;
  background: var(--vp-c-brand-soft);
  color: var(--vp-c-brand-1);
}

.lane-head.server {
  background: var(--vp-c-default-soft);
  color: var(--vp-c-text-1);
}

.sequence {
  position: relative;
  padding: 8px 0;
}

.lifelines span {
  position: absolute;
  top: 0;
  bottom: 0;
  border-left: 1px dashed var(--vp-c-divider);
}

.row {
  position: relative;
  height: 34px;
  cursor: pointer;
  opacity: 0.55;
  transition: opacity 0.2s;
}

.row.current {
  opacity: 1;
}

.arrow {
  position: absolute;
  top: 20px;
  height: 2px;
  background: var(--vp-c-text-2);
  animation: grow 0.45s ease-out;
}

.row.current .arrow {
  background: var(--vp-c-brand-1);
}

.arrow.direct {
  height: 4px;
  background: repeating-linear-gradient(90deg, var(--vp-c-brand-2) 0 10px, transparent 10px 16px);
}

.arrow.right {
  transform-origin: left;
}

.arrow.left {
  transform-origin: right;
}

.arrow::before,
.arrow::after {
  content: '';
  position: absolute;
  top: -4px;
  border: 5px solid transparent;
}

.arrow.right::after,
.arrow.both::after {
  right: -6px;
  border-left-color: currentColor;
}

.arrow.left::before,
.arrow.both::before {
  left: -6px;
  border-right-color: currentColor;
}

.arrow {
  color: var(--vp-c-text-2);
}

.row.current .arrow {
  color: var(--vp-c-brand-1);
}

.hop {
  position: absolute;
  top: -4px;
  left: calc(50% - 5px);
  width: 10px;
  height: 10px;
  border: 2px solid currentColor;
  border-radius: 50%;
  background: var(--vp-c-bg-soft);
}

.label {
  position: absolute;
  bottom: 4px;
  left: 50%;
  transform: translateX(-50%);
  padding: 0 6px;
  white-space: nowrap;
  font-family: var(--vp-font-family-mono);
  font-size: 12px;
  color: var(--vp-c-text-1);
  background: var(--vp-c-bg-soft);
}

.relayed .label {
  left: 25%;
}

.detail {
  min-height: 120px;
}

.explain {
  margin: 8px 0;
  animation: fade 0.3s ease-out;
}

.detail pre {
  margin: 0;
  padding: 10px 12px;
  overflow-x: auto;
  border-radius: 8px;
  background: var(--vp-code-block-bg);
  font-size: 12px;
  line-height: 1.5;
}

.controls {
  margin-top: 14px;
}

.toggle {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  margin-left: 4px;
  font-size: 13px;
}

.counter {
  margin-left: auto;
}

@keyframes grow {
  from {
    transform: scaleX(0);
  }
}

.arrow.both {
  transform-origin: center;
}

@keyframes fade {
  from {
    opacity: 0;
    transform: translateY(4px);
  }
}
</style>
