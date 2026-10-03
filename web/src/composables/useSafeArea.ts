import { onMounted, reactive } from 'vue'
import { useEventListener } from '@vueuse/core'

/** `env(safe-area-inset-*)` in pixels, for layout math done in script. */
export function useSafeArea() {
  const insets = reactive({ top: 0, right: 0, bottom: 0, left: 0 })
  let probe: HTMLDivElement | null = null

  function measure() {
    if (!probe) {
      probe = document.createElement('div')
      probe.style.cssText = 'position:fixed;visibility:hidden;pointer-events:none;'
        + 'padding:env(safe-area-inset-top) env(safe-area-inset-right) env(safe-area-inset-bottom) env(safe-area-inset-left);'
      document.body.appendChild(probe)
    }
    const style = getComputedStyle(probe)
    insets.top = parseFloat(style.paddingTop) || 0
    insets.right = parseFloat(style.paddingRight) || 0
    insets.bottom = parseFloat(style.paddingBottom) || 0
    insets.left = parseFloat(style.paddingLeft) || 0
  }

  onMounted(measure)
  useEventListener(window, 'resize', measure)
  useEventListener(window, 'orientationchange', measure)
  return insets
}
