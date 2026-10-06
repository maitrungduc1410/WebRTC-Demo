<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { useData } from 'vitepress'

const props = defineProps<{ code: string }>()
const { isDark } = useData()
const svg = ref('')
const failed = ref(false)
let count = 0

async function draw() {
  const { default: mermaid } = await import('mermaid')
  mermaid.initialize({
    startOnLoad: false,
    securityLevel: 'strict',
    theme: isDark.value ? 'dark' : 'default',
    fontFamily: 'var(--vp-font-family-base)',
  })
  const run = ++count
  try {
    const id = `mermaid-${Math.random().toString(36).slice(2)}-${run}`
    const result = (await mermaid.render(id, decodeURIComponent(props.code))).svg
    // A theme switch while rendering starts a newer draw; keep only the latest one.
    if (run !== count) return
    svg.value = result
    failed.value = false
  } catch (error) {
    if (run !== count) return
    failed.value = true
    console.error(error)
  }
}

onMounted(draw)
watch(isDark, draw)
</script>

<template>
  <div class="mermaid-diagram">
    <pre v-if="failed"><code>{{ decodeURIComponent(code) }}</code></pre>
    <div v-else-if="svg" v-html="svg" />
    <div v-else class="placeholder" />
  </div>
</template>

<style scoped>
.mermaid-diagram {
  margin: 16px 0;
  overflow-x: auto;
  text-align: center;
}

.mermaid-diagram :deep(svg) {
  max-width: 100%;
  height: auto;
}

.placeholder {
  min-height: 160px;
}
</style>
