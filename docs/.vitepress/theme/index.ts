import DefaultTheme from 'vitepress/theme'
import type { Theme } from 'vitepress'
import CallFlow from './components/CallFlow.vue'
import DemoMedia from './components/DemoMedia.vue'
import E2eeFrame from './components/E2eeFrame.vue'
import Mermaid from './components/Mermaid.vue'
import PlatformPicker from './components/PlatformPicker.vue'
import PortingChecklist from './components/PortingChecklist.vue'
import SfuCompare from './components/SfuCompare.vue'
import './style.css'

export default {
  extends: DefaultTheme,
  enhanceApp({ app }) {
    app.component('CallFlow', CallFlow)
    app.component('DemoMedia', DemoMedia)
    app.component('E2eeFrame', E2eeFrame)
    app.component('Mermaid', Mermaid)
    app.component('PlatformPicker', PlatformPicker)
    app.component('PortingChecklist', PortingChecklist)
    app.component('SfuCompare', SfuCompare)
  },
} satisfies Theme
