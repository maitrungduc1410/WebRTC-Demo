import { existsSync, readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import type { HeadConfig, PageData, TransformContext } from 'vitepress'

export const base = '/WebRTC-Demo/'
export const siteUrl = `https://maitrungduc1410.github.io${base}`

type Locale = 'en' | 'vi' | 'zh'

const locales: Record<Locale, { prefix: string; hreflang: string; ogLocale: string; image: string; imageAlt: string }> = {
  en: {
    prefix: '',
    hreflang: 'en',
    ogLocale: 'en_US',
    image: 'og-image.png',
    imageAlt: 'WebRTC Demo: video call apps for Web, Android, iOS, macOS and Windows',
  },
  vi: {
    prefix: 'vi/',
    hreflang: 'vi',
    ogLocale: 'vi_VN',
    image: 'og-image-vi.png',
    imageAlt: 'WebRTC Demo: app gọi video cho Web, Android, iOS, macOS và Windows',
  },
  zh: {
    prefix: 'zh/',
    hreflang: 'zh-Hans',
    ogLocale: 'zh_CN',
    image: 'og-image-zh.png',
    imageAlt: 'WebRTC Demo：Web、Android、iOS、macOS 和 Windows 视频通话应用',
  },
}

const localeOf = (relativePath: string): Locale =>
  relativePath.startsWith('vi/') ? 'vi' : relativePath.startsWith('zh/') ? 'zh' : 'en'

/** `vi/guide/index.md` -> `guide/index.md`, the same page in every language. */
const pageOf = (relativePath: string) => relativePath.slice(locales[localeOf(relativePath)].prefix.length)

/** The clean URL GitHub Pages serves for a page (`cleanUrls` is on). */
const urlOf = (locale: Locale, page: string) =>
  siteUrl + locales[locale].prefix + page.replace(/(^|\/)index\.md$/, '$1').replace(/\.md$/, '')

/** Plain text of the first paragraph under the page title, as a search result snippet. */
function firstParagraph(source: string, maxLength: number) {
  const body = source.replace(/^---\n[\s\S]*?\n---\n/, '')
  const paragraph = body
    .split(/\n\s*\n/)
    .map((block) => block.trim())
    .find((block) => block && !/^(#|<|\||```|:::|[-*+] |\d+\. |>|!\[|\[\[)/.test(block))
  if (!paragraph) return undefined
  const text = paragraph
    .replace(/\{#[^}]+\}/g, '')
    .replace(/!?\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/[*_`]/g, '')
    .replace(/<[^>]+>/g, '')
    .replace(/\s+/g, ' ')
    .trim()
  if (text.length <= maxLength) return text
  const cut = text.slice(0, maxLength)
  const sentence = Math.max(cut.lastIndexOf('. '), cut.lastIndexOf('。'), cut.lastIndexOf('！'), cut.lastIndexOf('？'))
  if (sentence > maxLength / 2) return cut.slice(0, sentence + 1)
  const space = cut.lastIndexOf(' ')
  return `${(space > maxLength / 2 ? cut.slice(0, space) : cut).replace(/[,，、:：;；]$/, '')}…`
}

/** VitePress writes the description meta tag without escaping it. */
const curlyQuotes = (text: string) => text.replace(/"([^"]*)"/g, '“$1”').replace(/"/g, '”')

/** Pages without a `description` in their frontmatter get their first paragraph. */
export function describePage(pageData: PageData, srcDir: string) {
  if (pageData.frontmatter.layout === 'home' || pageData.isNotFound) return
  const file = resolve(srcDir, pageData.relativePath)
  if (!pageData.description && existsSync(file)) {
    pageData.description = firstParagraph(readFileSync(file, 'utf8'), localeOf(pageData.relativePath) === 'zh' ? 90 : 160) ?? ''
  }
  pageData.description = curlyQuotes(pageData.description)
}

/**
 * sitemap.xml lists translations under the locales' `lang` (en-US, vi-VN, zh-CN). Use the same
 * region-free codes as the page heads, so a reader in Taiwan or Singapore still gets a match,
 * and add `x-default` (the English page).
 */
const hreflangOf: Record<string, string> = { 'en-US': locales.en.hreflang, 'vi-VN': locales.vi.hreflang, 'zh-CN': locales.zh.hreflang }

export function sitemapLanguages<T extends { links?: { lang: string; url: string }[] }>(items: T[]) {
  return items.map((item) => {
    if (!item.links || item.links.length < 2) return item
    const links = item.links.map((link) => ({ ...link, lang: hreflangOf[link.lang] ?? link.lang }))
    const english = links.find((link) => link.lang === locales.en.hreflang)
    return { ...item, links: english ? [...links, { lang: 'x-default', url: english.url }] : links }
  })
}

/** Canonical URL, language alternates, Open Graph and Twitter cards for one page. */
export function seoHead({ pageData, title, description, siteConfig }: TransformContext): HeadConfig[] {
  if (pageData.isNotFound || pageData.relativePath === '404.md') return [['meta', { name: 'robots', content: 'noindex' }]]

  const locale = localeOf(pageData.relativePath)
  const page = pageOf(pageData.relativePath)
  const url = urlOf(locale, page)
  const { ogLocale, image, imageAlt } = locales[locale]
  const isHome = pageData.frontmatter.layout === 'home'
  // og:site_name already names the site, so previews get the page title without " | WebRTC Demo".
  const shareTitle = isHome ? title : pageData.title || title

  const translated = (Object.keys(locales) as Locale[]).filter((other) =>
    existsSync(resolve(siteConfig.srcDir, locales[other].prefix + page)),
  )
  const head: HeadConfig[] = [['link', { rel: 'canonical', href: url }]]
  if (translated.length > 1) {
    for (const other of translated) {
      head.push(['link', { rel: 'alternate', hreflang: locales[other].hreflang, href: urlOf(other, page) }])
    }
    if (translated.includes('en')) head.push(['link', { rel: 'alternate', hreflang: 'x-default', href: urlOf('en', page) }])
  }
  head.push(
    ['meta', { property: 'og:type', content: isHome ? 'website' : 'article' }],
    ['meta', { property: 'og:url', content: url }],
    ['meta', { property: 'og:title', content: shareTitle }],
    ['meta', { property: 'og:description', content: description }],
    ['meta', { property: 'og:locale', content: ogLocale }],
    ...translated
      .filter((other) => other !== locale)
      .map((other): HeadConfig => ['meta', { property: 'og:locale:alternate', content: locales[other].ogLocale }]),
    ['meta', { property: 'og:image', content: siteUrl + image }],
    ['meta', { property: 'og:image:width', content: '1200' }],
    ['meta', { property: 'og:image:height', content: '630' }],
    ['meta', { property: 'og:image:alt', content: imageAlt }],
    ['meta', { name: 'twitter:title', content: shareTitle }],
    ['meta', { name: 'twitter:description', content: description }],
    ['meta', { name: 'twitter:image', content: siteUrl + image }],
    ['meta', { name: 'twitter:image:alt', content: imageAlt }],
  )
  if (!isHome && pageData.lastUpdated) {
    head.push(['meta', { property: 'article:modified_time', content: new Date(pageData.lastUpdated).toISOString() }])
  }
  if (isHome) {
    const jsonLd = {
      '@context': 'https://schema.org',
      '@graph': [
        { '@type': 'WebSite', name: 'WebRTC Demo', url: siteUrl, inLanguage: ['en', 'vi', 'zh-Hans'] },
        {
          '@type': 'SoftwareSourceCode',
          name: 'WebRTC Demo',
          description,
          url,
          codeRepository: 'https://github.com/maitrungduc1410/WebRTC-Demo',
          programmingLanguage: ['TypeScript', 'Kotlin', 'Swift', 'C#', 'Go'],
          runtimePlatform: ['Web', 'Android', 'iOS', 'macOS', 'Windows'],
          license: 'https://opensource.org/licenses/MIT',
          author: { '@type': 'Person', name: 'Mai Trung Duc', url: 'https://github.com/maitrungduc1410' },
        },
      ],
    }
    head.push(['script', { type: 'application/ld+json' }, JSON.stringify(jsonLd)])
  }
  return head
}
