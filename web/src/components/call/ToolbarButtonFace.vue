<script setup lang="ts">
import { computed, type Component } from 'vue'
import { AnimatePresence, motion } from 'motion-v'
import { cn } from '@/lib/utils'
import type { ToolbarTone } from './types'

const props = withDefaults(defineProps<{
  icon: Component
  label: string
  tone?: ToolbarTone
  badge?: number
  wide?: boolean
  pressed?: boolean
  disabled?: boolean
}>(), { tone: 'default', badge: 0 })

// Lucide icons are distinct component objects, so their identity keys the swap animation.
const iconIds = new WeakMap<Component, number>()
let nextIconId = 0
const iconKey = computed(() => {
  if (!iconIds.has(props.icon)) iconIds.set(props.icon, nextIconId++)
  return iconIds.get(props.icon)
})

const classes = computed(() => cn(
  'relative inline-flex h-12 shrink-0 items-center justify-center rounded-full outline-none select-none',
  'transition-[background-color,color,scale,box-shadow] duration-300 ease-bounce',
  'hover:scale-105 active:scale-90 active:duration-100 disabled:pointer-events-none disabled:opacity-40',
  'focus-visible:ring-3 focus-visible:ring-white/40',
  props.wide ? 'w-16 sm:w-[4.5rem]' : 'w-12',
  {
    default: 'bg-white/12 text-white hover:bg-white/20 data-[state=open]:bg-white/25',
    off: 'bg-red-500 text-white shadow-lg shadow-red-500/30 hover:bg-red-500/90',
    active: 'bg-indigo-500 text-white shadow-lg shadow-indigo-500/30 hover:bg-indigo-400',
    danger: 'bg-red-600 text-white shadow-lg shadow-red-600/30 hover:bg-red-500',
  }[props.tone],
))
</script>

<template>
  <button
    type="button"
    :class="classes"
    :aria-label="props.label"
    :aria-pressed="props.pressed"
    :disabled="props.disabled"
  >
    <AnimatePresence mode="popLayout" :initial="false">
      <motion.span
        :key="iconKey"
        class="flex"
        :initial="{ scale: 0.4, opacity: 0, rotate: -40 }"
        :animate="{ scale: 1, opacity: 1, rotate: 0 }"
        :exit="{ scale: 0.4, opacity: 0, rotate: 40 }"
        :transition="{ type: 'spring', stiffness: 520, damping: 28 }"
      >
        <component :is="props.icon" class="size-5" />
      </motion.span>
    </AnimatePresence>

    <AnimatePresence>
      <motion.span
        v-if="props.badge > 0"
        key="badge"
        class="absolute -top-1 -right-1 flex h-5 min-w-5 items-center justify-center rounded-full bg-red-500 px-1 text-[10px] font-bold text-white ring-2 ring-zinc-900"
        :initial="{ scale: 0 }"
        :animate="{ scale: 1 }"
        :exit="{ scale: 0 }"
        :transition="{ type: 'spring', stiffness: 600, damping: 22 }"
      >
        {{ props.badge > 9 ? '9+' : props.badge }}
      </motion.span>
    </AnimatePresence>
  </button>
</template>
