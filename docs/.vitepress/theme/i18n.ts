import { computed } from 'vue'
import { useData } from 'vitepress'

export type Locale = 'en' | 'vi' | 'zh'
export type Text = Record<Locale, string>

export function useLocale() {
  const { lang } = useData()
  return computed<Locale>(() => (lang.value.startsWith('vi') ? 'vi' : lang.value.startsWith('zh') ? 'zh' : 'en'))
}

/** Picks the current language's strings: `const t = useStrings({ en: {...}, vi: {...}, zh: {...} })`. */
export function useStrings<T>(strings: Record<Locale, T>) {
  const locale = useLocale()
  return computed(() => strings[locale.value])
}
