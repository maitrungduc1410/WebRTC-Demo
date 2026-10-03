<script setup lang="ts">
import type { Call } from '@/call/useCall'
import { Drawer, DrawerContent, DrawerDescription, DrawerHeader, DrawerTitle } from '@/components/ui/drawer'
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from '@/components/ui/sheet'
import EffectsPicker from './EffectsPicker.vue'

const open = defineModel<boolean>('open', { required: true })

defineProps<{
  call: Call
  /** Phones get a bottom drawer, wider screens a side sheet. */
  compact: boolean
}>()

const DESCRIPTION = 'Only your camera changes. The other person sees what your preview shows.'
</script>

<template>
  <Drawer v-if="compact" v-model:open="open">
    <DrawerContent class="dark max-h-[92dvh]">
      <DrawerHeader class="pb-2 text-left short:pt-1">
        <DrawerTitle>Backgrounds and effects</DrawerTitle>
        <DrawerDescription>{{ DESCRIPTION }}</DrawerDescription>
      </DrawerHeader>
      <div class="flex min-h-0 flex-1 flex-col px-4 pb-[max(env(safe-area-inset-bottom),1rem)]">
        <EffectsPicker :call="call" />
      </div>
    </DrawerContent>
  </Drawer>
  <Sheet v-else v-model:open="open">
    <SheetContent class="dark w-full gap-0 border-white/10 sm:max-w-[420px]">
      <SheetHeader class="pr-12">
        <SheetTitle>Backgrounds and effects</SheetTitle>
        <SheetDescription>{{ DESCRIPTION }}</SheetDescription>
      </SheetHeader>
      <div class="flex min-h-0 flex-1 flex-col px-4 pb-4">
        <EffectsPicker :call="call" />
      </div>
    </SheetContent>
  </Sheet>
</template>
