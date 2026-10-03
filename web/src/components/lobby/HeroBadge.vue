<script setup lang="ts">
import { Video } from '@lucide/vue'
</script>

<template>
  <!-- The app mark: a disc with a breathing camera and a slowly turning halo. -->
  <div class="relative size-28 shrink-0 short:size-20 sm:size-32" aria-hidden="true">
    <div class="absolute -inset-1 animate-spin-slow rounded-full bg-[conic-gradient(from_0deg,#818cf8,#c084fc,#60a5fa,#818cf8)] opacity-80 blur-[2px]" />
    <div class="absolute inset-[3px] rounded-full bg-background" />
    <div class="absolute inset-3 flex items-center justify-center rounded-full bg-gradient-to-br from-indigo-500 to-violet-600 shadow-2xl shadow-indigo-500/40 short:inset-2">
      <Video class="size-11 animate-breathe text-white short:size-8 sm:size-12" />
    </div>
  </div>
</template>
