<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useData } from 'vitepress'
import { useStrings } from '../i18n'
import Icon from './Icon.vue'
import MermaidDialog from './MermaidDialog.vue'

const props = defineProps<{ code: string }>()
const { isDark } = useData()
const source = decodeURIComponent(props.code)
const svg = ref('')
const svgId = ref('')
const failed = ref(false)
const open = ref(false)
const copied = ref(false)
let count = 0
let copiedTimer: ReturnType<typeof setTimeout> | undefined

const t = useStrings({
  en: { open: 'Open diagram', copy: 'Copy Mermaid source', copied: 'Copied' },
  vi: { open: 'Mở sơ đồ', copy: 'Copy mã Mermaid', copied: 'Đã copy' },
  zh: { open: '打开图表', copy: '复制 Mermaid 源码', copied: '已复制' },
})

const darkTheme = {
  darkMode: true,
  background: '#1b1b1f',
  primaryColor: '#26244a',
  primaryBorderColor: '#818cf8',
  primaryTextColor: '#e0e7ff',
  secondaryColor: '#2e1f4f',
  secondaryBorderColor: '#a78bfa',
  secondaryTextColor: '#ede9fe',
  tertiaryColor: '#1f1d33',
  tertiaryBorderColor: '#4f46e5',
  tertiaryTextColor: '#e0e7ff',
  lineColor: '#a5b4fc',
  textColor: '#e5e7eb',
  titleColor: '#c7d2fe',
  edgeLabelBackground: '#2a2840',
  clusterBkg: '#1f1d33',
  clusterBorder: '#4f46e5',
  noteBkgColor: '#3b3215',
  noteBorderColor: '#ca8a04',
  noteTextColor: '#fef3c7',
  actorBkg: '#26244a',
  actorBorder: '#818cf8',
  actorTextColor: '#e0e7ff',
  actorLineColor: '#6366f1',
  signalColor: '#c7d2fe',
  signalTextColor: '#e5e7eb',
  labelBoxBkgColor: '#26244a',
  labelBoxBorderColor: '#818cf8',
  labelTextColor: '#e0e7ff',
  loopTextColor: '#e5e7eb',
  activationBkgColor: '#312e81',
  activationBorderColor: '#818cf8',
  sequenceNumberColor: '#1b1b1f',
}

async function draw() {
  const run = ++count
  const { default: mermaid } = await import('mermaid')
  // Mermaid sizes every box by measuring its label, so it needs a real font name (not a CSS variable)
  // and that font must be loaded first, or the text comes out wider than its box and gets cut off.
  const fontFamily =
    getComputedStyle(document.documentElement).getPropertyValue('--vp-font-family-base').trim() || 'sans-serif'
  // Passing the diagram text makes the browser fetch every subset it needs, like Inter's Vietnamese one.
  await Promise.all([
    document.fonts.load(`16px ${fontFamily}`, source),
    document.fonts.load(`bold 16px ${fontFamily}`, source),
  ]).catch(() => {})
  await document.fonts.ready
  if (run !== count) return
  mermaid.initialize({
    startOnLoad: false,
    securityLevel: 'strict',
    fontFamily,
    theme: isDark.value ? 'base' : 'default',
    themeVariables: isDark.value ? { ...darkTheme, fontFamily } : { fontFamily },
  })
  try {
    const id = `mermaid-${Math.random().toString(36).slice(2)}-${run}`
    const result = (await mermaid.render(id, source)).svg
    // A theme switch while rendering starts a newer draw; keep only the latest one.
    if (run !== count) return
    svg.value = result
    svgId.value = id
    failed.value = false
  } catch (error) {
    if (run !== count) return
    failed.value = true
    console.error(error)
  }
}

async function copy() {
  try {
    await navigator.clipboard.writeText(source)
  } catch {
    return
  }
  copied.value = true
  clearTimeout(copiedTimer)
  copiedTimer = setTimeout(() => (copied.value = false), 1500)
}

onMounted(draw)
watch(isDark, draw)
onBeforeUnmount(() => clearTimeout(copiedTimer))
</script>

<template>
  <div class="mermaid-diagram">
    <pre v-if="failed"><code>{{ source }}</code></pre>
    <template v-else-if="svg">
      <span class="visually-hidden" aria-live="polite">{{ copied ? t.copied : '' }}</span>
      <div class="actions">
        <button type="button" :title="copied ? t.copied : t.copy" :aria-label="copied ? t.copied : t.copy" @click="copy">
          <Icon :name="copied ? 'check' : 'copy'" />
        </button>
        <button type="button" :title="t.open" :aria-label="t.open" @click="open = true">
          <Icon name="expand" />
        </button>
      </div>
      <div class="drawing" @click="open = true" v-html="svg" />
      <MermaidDialog v-model:open="open" :svg="svg" :svg-id="svgId" :source="source" />
    </template>
    <div v-else class="placeholder" />
  </div>
</template>

<style scoped>
.mermaid-diagram {
  position: relative;
  margin: 16px 0;
  border: 1px solid var(--vp-c-divider);
  border-radius: 8px;
  padding: 16px;
  text-align: center;
}

.drawing {
  overflow-x: auto;
  cursor: zoom-in;
}

.drawing :deep(svg) {
  max-width: 100%;
  height: auto;
}

/* Mermaid sizes labels with its own line-height; the 28px from .vp-doc p would make them taller than their boxes. */
.drawing :deep(svg p) {
  line-height: inherit;
}

.actions {
  position: absolute;
  top: 8px;
  right: 8px;
  z-index: 1;
  display: flex;
  gap: 4px;
  opacity: 0;
  transition: opacity 0.2s;
}

.mermaid-diagram:hover .actions,
.actions:focus-within {
  opacity: 1;
}

@media (hover: none) {
  .actions {
    opacity: 1;
  }
}

.actions button {
  display: grid;
  place-items: center;
  width: 32px;
  height: 32px;
  border: 1px solid var(--vp-c-divider);
  border-radius: 6px;
  background: var(--vp-c-bg);
  color: var(--vp-c-text-2);
}

.actions button:hover {
  color: var(--vp-c-brand-1);
  border-color: var(--vp-c-brand-1);
}

.placeholder {
  min-height: 160px;
}
</style>
