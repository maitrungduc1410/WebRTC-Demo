import { useMediaQuery } from '@vueuse/core'

/** Phones and tablets: no hover, so tooltips and keyboard hints only get in the way. */
export function useCoarsePointer() {
  return useMediaQuery('(pointer: coarse)')
}

/** Room for the chat as a side panel next to the stage. */
export function useWideLayout() {
  return useMediaQuery('(min-width: 1024px)')
}

/** Phone-sized controls; also a phone held sideways. */
export function useCompactLayout() {
  return useMediaQuery('(max-width: 639px), (max-height: 540px)')
}
