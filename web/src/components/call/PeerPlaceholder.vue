<script setup lang="ts">
import { computed } from 'vue'
import { User } from '@lucide/vue'
import { cn } from '@/lib/utils'

const props = withDefaults(defineProps<{
  snapshot?: string | null
  /** 0..1 from the receiver's audio level; the ring around the avatar follows it. */
  audioLevel?: number
  title?: string
  subtitle?: string
  size?: 'sm' | 'md' | 'lg'
  class?: string
}>(), { snapshot: null, audioLevel: 0, size: 'lg' })

const level = computed(() => Math.min(props.audioLevel * 3, 1))
const avatar = computed(() => ({ sm: 'size-12', md: 'size-20', lg: 'size-28' })[props.size])
const icon = computed(() => ({ sm: 'size-6', md: 'size-9', lg: 'size-14' })[props.size])
</script>

<template>
  <div :class="cn('absolute inset-0 flex items-center justify-center overflow-hidden bg-zinc-950', props.class)">
    <div
      v-if="props.snapshot"
      class="absolute inset-0 scale-125 bg-cover bg-center blur-3xl"
      :style="{ backgroundImage: `url(${props.snapshot})` }"
    />
    <div v-else class="absolute inset-0 bg-[radial-gradient(circle_at_30%_20%,oklch(0.45_0.2_290/0.6),transparent_60%),radial-gradient(circle_at_70%_80%,oklch(0.45_0.18_255/0.55),transparent_55%)]" />
    <div class="absolute inset-0 bg-black/35" />

    <div class="relative flex flex-col items-center gap-4 px-6 text-center">
      <div :class="cn('relative', avatar)">
        <div
          class="absolute inset-0 rounded-full bg-indigo-400 transition-[transform,opacity] duration-150 ease-out"
          :style="{ transform: `scale(${1.06 + level * 0.55})`, opacity: 0.16 + level * 0.5 }"
        />
        <div class="absolute inset-0 flex items-center justify-center rounded-full bg-gradient-to-br from-violet-500 to-indigo-500 shadow-2xl ring-1 ring-white/20">
          <User :class="cn('text-white', icon)" />
        </div>
      </div>
      <div v-if="props.title || props.subtitle" class="space-y-1">
        <p v-if="props.title" class="font-semibold text-white">{{ props.title }}</p>
        <p v-if="props.subtitle" class="text-sm text-white/70">{{ props.subtitle }}</p>
      </div>
      <slot />
    </div>
  </div>
</template>
