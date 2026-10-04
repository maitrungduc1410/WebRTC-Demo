<script setup lang="ts">
import { computed, nextTick, ref } from 'vue'
import { AnimatePresence, motion } from 'motion-v'
import { Check, Pencil, RotateCcw, X } from '@lucide/vue'
import type { ServerStatus } from '@/call/types'
import { Button } from '@/components/ui/button'
import { Kbd } from '@/components/ui/kbd'
import { cn } from '@/lib/utils'

const props = defineProps<{
  joinHint: boolean
  /** "Signaling server", or "Group call server" in group mode */
  label: string
  url: string
  status: ServerStatus
  defaultUrl: string
  placeholder: string
  /** Normalizes, saves and applies the address; false when it is not valid. */
  setUrl: (input: string) => boolean
}>()

const editing = ref(false)
const draft = ref('')
const invalid = ref(false)
const input = ref<HTMLInputElement>()

const status = computed(() => ({
  connected: { dot: 'bg-emerald-500', text: 'Connected' },
  connecting: { dot: 'bg-amber-400 animate-pulse', text: 'Connecting…' },
  unreachable: { dot: 'bg-red-500', text: "Can't reach this server" },
})[props.status])

async function edit() {
  draft.value = props.url
  invalid.value = false
  editing.value = true
  await nextTick()
  input.value?.select()
}

function save() {
  invalid.value = !props.setUrl(draft.value)
  if (!invalid.value) editing.value = false
}

/** Applies an open editor before joining; false keeps the user in the lobby to fix the address. */
function commit() {
  if (!editing.value) return true
  save()
  return !invalid.value
}

defineExpose({ commit })

function useDefault() {
  draft.value = props.defaultUrl
  save()
}

const panel = {
  initial: { height: 0, opacity: 0 },
  animate: { height: 'auto', opacity: 1 },
  exit: { height: 0, opacity: 0 },
  transition: { type: 'spring', stiffness: 380, damping: 34 },
}
</script>

<template>
  <div class="mt-5 text-xs text-muted-foreground">
    <div class="flex items-center justify-between gap-3">
      <button
        type="button"
        :class="cn(
          'group -ml-2 inline-flex min-w-0 items-center gap-2 rounded-full py-1 pr-2.5 pl-2 transition-colors duration-200',
          'hover:bg-muted hover:text-foreground focus-visible:ring-3 focus-visible:ring-ring/50 focus-visible:outline-none',
          editing && 'bg-muted text-foreground',
        )"
        :title="status.text"
        :aria-label="`${props.label} ${props.url}, ${status.text}. Change`"
        :aria-expanded="editing"
        @click="editing ? (editing = false) : edit()"
      >
        <span class="relative flex size-2 shrink-0">
          <span
            v-if="props.status === 'connected'"
            class="absolute inset-0 animate-ripple rounded-full bg-emerald-500"
          />
          <span :class="cn('relative size-2 rounded-full transition-colors duration-500', status.dot)" />
        </span>
        <span class="truncate font-mono">{{ props.url }}</span>
        <Pencil class="size-3 shrink-0 opacity-50 transition-opacity group-hover:opacity-100" />
      </button>
      <Transition
        enter-active-class="transition duration-300"
        leave-active-class="transition duration-150"
        enter-from-class="opacity-0"
        leave-to-class="opacity-0"
      >
        <span v-if="props.joinHint && !editing" class="inline-flex shrink-0 items-center gap-1">Press <Kbd>Enter</Kbd> to join</span>
      </Transition>
    </div>

    <AnimatePresence :initial="false">
      <motion.p
        v-if="props.status === 'unreachable' && !editing"
        key="unreachable"
        v-bind="panel"
        class="overflow-hidden text-destructive"
      >
        <span class="block pt-2">Can't reach the {{ props.label.toLowerCase() }}. Start it, or tap the address to change it.</span>
      </motion.p>

      <motion.div v-if="editing" key="editor" v-bind="panel" class="overflow-hidden">
        <div class="pt-3 pb-0.5">
          <label for="server" class="font-medium">{{ props.label }}</label>
          <div class="mt-2 flex items-center gap-2">
            <input
              id="server"
              ref="input"
              v-model="draft"
              inputmode="url"
              autocomplete="off"
              autocapitalize="off"
              spellcheck="false"
              enterkeyhint="done"
              :placeholder="props.placeholder"
              :aria-invalid="invalid"
              class="h-10 min-w-0 flex-1 rounded-xl border border-input bg-input/30 px-3 font-mono text-base text-foreground outline-none transition-[border-color,box-shadow] duration-200 placeholder:text-muted-foreground/50 focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 aria-invalid:border-destructive aria-invalid:ring-destructive/20 sm:text-sm"
              @input="invalid = false"
              @keydown.enter.prevent="save"
              @keydown.esc.prevent.stop="editing = false"
            >
            <Button type="button" size="icon" class="size-10 shrink-0 rounded-xl" aria-label="Save server" @click="save">
              <Check />
            </Button>
            <Button type="button" variant="ghost" size="icon" class="size-10 shrink-0 rounded-xl" aria-label="Cancel" @click="editing = false">
              <X />
            </Button>
          </div>
          <div class="mt-2 flex items-center justify-between gap-3">
            <span :class="cn('transition-colors', invalid && 'text-destructive')">
              {{ invalid ? `Use an address like ${props.placeholder}` : `${status.text} · saved on this device` }}
            </span>
            <button
              v-if="props.url !== props.defaultUrl"
              type="button"
              class="inline-flex shrink-0 items-center gap-1 rounded-full px-1.5 py-0.5 transition-colors hover:bg-muted hover:text-foreground"
              @click="useDefault"
            >
              <RotateCcw class="size-3" /> Use default
            </button>
          </div>
        </div>
      </motion.div>
    </AnimatePresence>
  </div>
</template>
