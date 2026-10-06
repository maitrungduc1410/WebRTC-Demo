<script setup lang="ts">
import { onMounted, ref, useSlots, type VNode } from 'vue'
import { withBase } from 'vitepress'
import { useStrings } from '../i18n'

/**
 * A screenshot or a recording from docs/public. Until the file is there it shows what to
 * capture (the slot), so adding the file is enough: no page needs to change. Once the file
 * is there, that note becomes its description for screen readers unless `alt` is given.
 */
const props = withDefaults(
  defineProps<{ src: string; kind?: 'image' | 'video'; alt?: string; width?: number; poster?: string }>(),
  { kind: 'image', alt: '', width: 0, poster: '' },
)

const t = useStrings({
  en: { image: 'Screenshot to add', video: 'Recording to add', file: 'File' },
  vi: { image: 'Ảnh chụp cần thêm', video: 'Video cần quay', file: 'File' },
  zh: { image: '待添加截图', video: '待添加录屏', file: '文件' },
})

const slots = useSlots()
const textOf = (nodes: VNode[] = []): string =>
  nodes
    .map((node) => (typeof node.children === 'string' ? node.children : Array.isArray(node.children) ? textOf(node.children as VNode[]) : ''))
    .join('')
const description = () => props.alt || textOf(slots.default?.()).replace(/\s+/g, ' ').trim()

const present = ref(false)
const url = withBase(props.src)

onMounted(async () => {
  try {
    const response = await fetch(url, { method: 'HEAD' })
    const type = response.headers.get('content-type') ?? ''
    present.value = response.ok && (type.startsWith('image/') || type.startsWith('video/'))
  } catch {
    present.value = false
  }
})
</script>

<template>
  <figure class="demo-media" :style="width ? { maxWidth: `${width}px` } : undefined">
    <template v-if="present">
      <video
        v-if="kind === 'video'"
        :src="url"
        :poster="poster ? withBase(poster) : undefined"
        autoplay
        muted
        loop
        playsinline
        controls
        :aria-label="description()"
      />
      <img v-else :src="url" :alt="description()" loading="lazy" />
    </template>
    <div v-else class="missing">
      <div class="label">{{ kind === 'video' ? t.video : t.image }}</div>
      <div class="note"><slot /></div>
      <code class="file">{{ t.file }}: docs/public{{ src }}</code>
    </div>
  </figure>
</template>

<style scoped>
.demo-media {
  margin: 20px auto;
}

.demo-media img,
.demo-media video {
  display: block;
  width: 100%;
  border-radius: 12px;
  box-shadow: var(--vp-shadow-2);
}

.missing {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 20px;
  border: 2px dashed var(--vp-c-divider);
  border-radius: 12px;
  background: var(--vp-c-bg-soft);
}

.label {
  color: var(--vp-c-brand-1);
  font-size: 12px;
  font-weight: 600;
  letter-spacing: 0.04em;
  text-transform: uppercase;
}

.note {
  font-size: 14px;
  line-height: 1.6;
}

.note :deep(p) {
  margin: 0;
}

.file {
  align-self: flex-start;
  font-size: 12px;
}
</style>
