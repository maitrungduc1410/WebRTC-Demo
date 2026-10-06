<script setup lang="ts">
import { computed, ref } from 'vue'
import { useStrings } from '../i18n'

const people = ref(5)
const bitrate = 1.5

const t = useStrings({
  en: {
    title: 'Mesh or SFU?',
    people: 'People in the call',
    mesh: 'Mesh: everyone connects to everyone',
    sfu: 'SFU: everyone connects to the server',
    connections: 'Connections per client',
    encodes: 'Video encodes and uploads per client',
    downloads: 'Videos each client downloads',
    upload: 'Upload per client at 1.5 Mbps per 720p video',
    server: 'Streams the server sends',
    none: 'no server',
  },
  vi: {
    title: 'Mesh hay SFU?',
    people: 'Số người trong cuộc gọi',
    mesh: 'Mesh: ai cũng kết nối tới tất cả những người khác',
    sfu: 'SFU: ai cũng chỉ kết nối tới server',
    connections: 'Số kết nối của mỗi client',
    encodes: 'Số lần encode và upload video của mỗi client',
    downloads: 'Số video mỗi client tải về',
    upload: 'Upload của mỗi client, với 1.5 Mbps cho mỗi video 720p',
    server: 'Số stream server gửi đi',
    none: 'không có server',
  },
  zh: {
    title: 'Mesh 还是 SFU？',
    people: '通话人数',
    mesh: 'Mesh：每个人都和其他所有人直连',
    sfu: 'SFU：每个人只连接服务器',
    connections: '每个客户端的连接数',
    encodes: '每个客户端编码并上传的视频路数',
    downloads: '每个客户端下载的视频路数',
    upload: '每个客户端的上行带宽（每路 720p 视频按 1.5 Mbps 算）',
    server: '服务器发出的流数量',
    none: '没有服务器',
  },
})

const size = 220
const center = size / 2
const radius = 84

const nodes = computed(() =>
  Array.from({ length: people.value }, (_, i) => {
    const angle = (i / people.value) * Math.PI * 2 - Math.PI / 2
    return { x: center + radius * Math.cos(angle), y: center + radius * Math.sin(angle) }
  }),
)

const meshEdges = computed(() => {
  const edges: [number, number][] = []
  for (let a = 0; a < people.value; a++) for (let b = a + 1; b < people.value; b++) edges.push([a, b])
  return edges
})

const rows = computed(() => {
  const n = people.value
  return [
    { label: t.value.connections, mesh: n - 1, sfu: 2 },
    { label: t.value.encodes, mesh: n - 1, sfu: 1 },
    { label: t.value.downloads, mesh: n - 1, sfu: n - 1 },
    { label: t.value.upload, mesh: `${((n - 1) * bitrate).toFixed(1)} Mbps`, sfu: `${bitrate.toFixed(1)} Mbps` },
    { label: t.value.server, mesh: t.value.none, sfu: n * (n - 1) },
  ]
})
</script>

<template>
  <div class="demo-card sfu-compare">
    <h4>{{ t.title }}</h4>
    <label class="demo-row slider">
      <span>{{ t.people }}</span>
      <input v-model.number="people" type="range" min="2" max="10" />
      <strong>{{ people }}</strong>
    </label>
    <div class="diagrams">
      <figure>
        <svg :viewBox="`0 0 ${size} ${size}`">
          <line v-for="[a, b] in meshEdges" :key="`${a}-${b}`" :x1="nodes[a].x" :y1="nodes[a].y" :x2="nodes[b].x" :y2="nodes[b].y" class="edge" />
          <circle v-for="(node, i) in nodes" :key="i" :cx="node.x" :cy="node.y" r="11" class="node" />
        </svg>
        <figcaption>{{ t.mesh }}</figcaption>
      </figure>
      <figure>
        <svg :viewBox="`0 0 ${size} ${size}`">
          <line v-for="(node, i) in nodes" :key="i" :x1="center" :y1="center" :x2="node.x" :y2="node.y" class="edge sfu" />
          <rect :x="center - 22" :y="center - 15" width="44" height="30" rx="7" class="server" />
          <text :x="center" :y="center + 4" text-anchor="middle" class="server-label">SFU</text>
          <circle v-for="(node, i) in nodes" :key="`n${i}`" :cx="node.x" :cy="node.y" r="11" class="node" />
        </svg>
        <figcaption>{{ t.sfu }}</figcaption>
      </figure>
    </div>
    <table>
      <thead>
        <tr><th /><th>Mesh</th><th>SFU</th></tr>
      </thead>
      <tbody>
        <tr v-for="row in rows" :key="row.label">
          <td>{{ row.label }}</td>
          <td>{{ row.mesh }}</td>
          <td>{{ row.sfu }}</td>
        </tr>
      </tbody>
    </table>
  </div>
</template>

<style scoped>
.slider {
  font-size: 14px;
}

.slider input {
  flex: 1;
  max-width: 260px;
  accent-color: var(--vp-c-brand-1);
}

.diagrams {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
  gap: 12px;
  margin-top: 8px;
}

figure {
  margin: 0;
  text-align: center;
}

svg {
  width: 100%;
  max-width: 240px;
}

figcaption {
  color: var(--vp-c-text-2);
  font-size: 13px;
}

.edge {
  stroke: var(--vp-c-text-3);
  stroke-width: 1.5;
}

.edge.sfu {
  stroke: var(--vp-c-brand-2);
  stroke-width: 2.5;
}

.node {
  fill: var(--vp-c-brand-1);
  stroke: var(--vp-c-bg-soft);
  stroke-width: 3;
}

.server {
  fill: var(--vp-c-bg);
  stroke: var(--vp-c-brand-2);
  stroke-width: 2;
}

.server-label {
  fill: var(--vp-c-text-1);
  font-size: 12px;
  font-weight: 600;
}

table {
  display: table;
  width: 100%;
  margin: 12px 0 0;
}

td:not(:first-child),
th:not(:first-child) {
  text-align: center;
  white-space: nowrap;
}
</style>
