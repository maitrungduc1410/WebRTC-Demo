<script setup lang="ts">
import { computed, useTemplateRef, type Component } from 'vue'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'
import { DropdownMenu, DropdownMenuContent, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { Kbd } from '@/components/ui/kbd'
import { useCoarsePointer } from '@/composables/useInputMode'
import ToolbarButtonFace from './ToolbarButtonFace.vue'
import type { ToolbarTone } from './types'

const props = withDefaults(defineProps<{
  icon: Component
  label: string
  tone?: ToolbarTone
  shortcut?: string
  badge?: number
  wide?: boolean
  pressed?: boolean
  disabled?: boolean
  /** With a #menu slot the button opens a dropdown instead of emitting click. */
  menuAlign?: 'start' | 'center' | 'end'
}>(), { tone: 'default', badge: 0, menuAlign: 'center' })

const emit = defineEmits<{ click: [] }>()
const slots = defineSlots<{ menu?: () => unknown }>()
const coarse = useCoarsePointer()

// The trigger sits inside the tooltip's popper scope, so the menu would anchor to nothing; point it at the button.
const face = useTemplateRef<InstanceType<typeof ToolbarButtonFace>>('face')
const menuReference = computed(() => face.value?.$el as HTMLElement | undefined)
</script>

<template>
  <DropdownMenu v-if="slots.menu">
    <Tooltip :disabled="coarse" :delay-duration="250">
      <TooltipTrigger as-child>
        <DropdownMenuTrigger as-child>
          <ToolbarButtonFace
            ref="face"
            :icon="props.icon"
            :label="props.label"
            :tone="props.tone"
            :badge="props.badge"
            :wide="props.wide"
            :pressed="props.pressed"
            :disabled="props.disabled"
          />
        </DropdownMenuTrigger>
      </TooltipTrigger>
      <TooltipContent side="top" :side-offset="10">
        {{ props.label }}
        <Kbd v-if="props.shortcut">{{ props.shortcut }}</Kbd>
      </TooltipContent>
    </Tooltip>
    <DropdownMenuContent side="top" :align="props.menuAlign" :side-offset="12" :reference="menuReference" class="min-w-72">
      <slot name="menu" />
    </DropdownMenuContent>
  </DropdownMenu>

  <Tooltip v-else :disabled="coarse" :delay-duration="250">
    <TooltipTrigger as-child>
      <ToolbarButtonFace
        :icon="props.icon"
        :label="props.label"
        :tone="props.tone"
        :badge="props.badge"
        :wide="props.wide"
        :pressed="props.pressed"
        :disabled="props.disabled"
        @click="emit('click')"
      />
    </TooltipTrigger>
    <TooltipContent side="top" :side-offset="10">
      {{ props.label }}
      <Kbd v-if="props.shortcut">{{ props.shortcut }}</Kbd>
    </TooltipContent>
  </Tooltip>
</template>
