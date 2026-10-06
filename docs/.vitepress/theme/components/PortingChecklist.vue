<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useLocale, useStrings } from '../i18n'

/**
 * Turns the list items of the Markdown inside it into checkboxes and remembers them in
 * localStorage, so the list itself stays plain Markdown in every language.
 * Items are saved by position, so each language keeps its own list.
 */
const locale = useLocale()
const storageKey = () => `webrtc-demo-porting-checklist-${locale.value}`
const root = ref<HTMLElement>()
const total = ref(0)
const done = ref(0)
let boxes: HTMLInputElement[] = []

const t = useStrings({
  en: { done: (d: number, n: number) => `${d} of ${n} done`, reset: 'Clear', saved: 'Saved in this browser' },
  vi: { done: (d: number, n: number) => `Xong ${d}/${n}`, reset: 'Xóa hết', saved: 'Lưu trên trình duyệt này' },
  zh: { done: (d: number, n: number) => `已完成 ${d}/${n}`, reset: '清空', saved: '保存在当前浏览器中' },
})

function save() {
  const checked = boxes.flatMap((box, i) => (box.checked ? [i] : []))
  localStorage.setItem(storageKey(), JSON.stringify(checked))
  done.value = checked.length
}

function reset() {
  boxes.forEach((box) => (box.checked = false))
  save()
}

onMounted(() => {
  let saved: number[] = []
  try {
    saved = JSON.parse(localStorage.getItem(storageKey()) ?? '[]')
  } catch {}
  const items = [...root.value!.querySelectorAll('li')].filter((li) => !li.parentElement?.closest('li'))
  boxes = items.map((li, i) => {
    const box = document.createElement('input')
    box.type = 'checkbox'
    box.checked = saved.includes(i)
    box.setAttribute('aria-label', li.textContent?.replace(/\s+/g, ' ').trim() ?? '')
    box.addEventListener('change', save)
    li.classList.add('check-item')
    li.prepend(box)
    li.addEventListener('click', (event) => {
      if (event.target === box || (event.target as HTMLElement).closest('a, code')) return
      box.checked = !box.checked
      save()
    })
    return box
  })
  total.value = boxes.length
  done.value = boxes.filter((box) => box.checked).length
})
</script>

<template>
  <div class="porting-checklist">
    <div class="progress">
      <div class="bar"><span :style="{ width: total ? `${(done / total) * 100}%` : '0' }" /></div>
      <span class="count">{{ t.done(done, total) }}</span>
      <span class="demo-muted">{{ t.saved }}</span>
      <button class="demo-button" :disabled="!done" @click="reset">{{ t.reset }}</button>
    </div>
    <div ref="root"><slot /></div>
  </div>
</template>

<style scoped>
.progress {
  position: sticky;
  top: calc(var(--vp-nav-height) + 8px);
  z-index: 2;
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 10px;
  margin: 16px 0;
  padding: 10px 14px;
  border: 1px solid var(--vp-c-divider);
  border-radius: 12px;
  background: var(--vp-c-bg-soft);
}

.bar {
  flex: 1;
  min-width: 120px;
  height: 8px;
  overflow: hidden;
  border-radius: 999px;
  background: var(--vp-c-default-soft);
}

.bar span {
  display: block;
  height: 100%;
  background: linear-gradient(90deg, var(--vp-c-brand-2), #8b5cf6);
  transition: width 0.3s ease;
}

.count {
  font-size: 14px;
  font-weight: 600;
}

:deep(ul) {
  padding-left: 0;
  list-style: none;
}

:deep(li.check-item) {
  position: relative;
  padding: 4px 0 4px 30px;
  cursor: pointer;
}

:deep(li.check-item > input) {
  position: absolute;
  top: 9px;
  left: 4px;
  width: 16px;
  height: 16px;
  accent-color: var(--vp-c-brand-1);
  cursor: pointer;
}

:deep(li.check-item:has(> input:checked)) {
  color: var(--vp-c-text-2);
}
</style>
