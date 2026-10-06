import { existsSync, readFileSync, statSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { defineConfig, type DefaultTheme } from 'vitepress'
import type MarkdownIt from 'markdown-it'

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '../..')
const base = '/WebRTC-Demo/'
const repoUrl = 'https://github.com/maitrungduc1410/WebRTC-Demo'

/**
 * `[text](gh:path/in/repo)` links to the file on GitHub. The build fails when the path
 * is not in the repository, so renamed files can't leave dead links behind.
 */
function repoLinks(md: MarkdownIt) {
  md.core.ruler.push('repo_links', (state) => {
    for (const block of state.tokens) {
      for (const token of block.children ?? []) {
        const href = token.type === 'link_open' ? token.attrGet('href') : null
        if (!href?.startsWith('gh:')) continue
        const path = href.slice(3)
        const file = resolve(repoRoot, path.split('#')[0])
        if (!existsSync(file)) {
          throw new Error(`${state.env.relativePath}: "${path}" is not in the repository`)
        }
        token.attrSet('href', `${repoUrl}/${statSync(file).isDirectory() ? 'tree' : 'blob'}/master/${path}`)
      }
    }
  })
}

/**
 * Every heading has an explicit `{#id}` (the English slug, kept in every language), so a link
 * like `/how-it-works/e2ee#the-key` must find `{#the-key}` in that page, or the build fails.
 */
function anchorLinks(md: MarkdownIt) {
  const docsRoot = resolve(repoRoot, 'docs')
  md.core.ruler.push('anchor_links', (state) => {
    for (const block of state.tokens) {
      for (const token of block.children ?? []) {
        const href = token.type === 'link_open' ? token.attrGet('href') : null
        if (!href || !href.includes('#') || /^[a-z]+:/i.test(href) || token.attrGet('class') === 'header-anchor') continue
        const [path, anchor] = href.split('#')
        const from = path.startsWith('/') ? docsRoot : resolve(docsRoot, dirname(state.env.relativePath))
        const page = path
          ? [`${path.replace(/\/$/, '/index')}.md`, `${path.replace(/\.md$/, '')}.md`, `${path}/index.md`].map((p) => resolve(from, p.replace(/^\//, ''))).find(existsSync)
          : resolve(docsRoot, state.env.relativePath)
        if (!page) throw new Error(`${state.env.relativePath}: "${href}" points to a page that does not exist`)
        if (!readFileSync(page, 'utf8').includes(`{#${anchor}}`)) {
          throw new Error(`${state.env.relativePath}: "${href}" has no heading {#${anchor}}`)
        }
      }
    }
  })
}

/** ```mermaid blocks become a component that draws the diagram in the browser. */
function mermaidFences(md: MarkdownIt) {
  const fence = md.renderer.rules.fence!
  md.renderer.rules.fence = (tokens, index, options, env, self) => {
    const token = tokens[index]
    if (token.info.trim() !== 'mermaid') return fence(tokens, index, options, env, self)
    return `<Mermaid code="${encodeURIComponent(token.content)}" />`
  }
}

interface Labels {
  guide: string
  whatIsThis: string
  quickStart: string
  groupCalls: string
  usingTheApp: string
  troubleshooting: string
  howItWorks: string
  overview: string
  signaling: string
  mediaState: string
  mediaSources: string
  chat: string
  pip: string
  e2ee: string
  sfu: string
  effects: string
  platforms: string
  buildYourOwn: string
  porting: string
  customize: string
  reference: string
  messages: string
  versions: string
}

function sidebar(prefix: string, t: Labels): DefaultTheme.Sidebar {
  return [
    {
      text: t.guide,
      items: [
        { text: t.whatIsThis, link: `${prefix}/guide/` },
        { text: t.quickStart, link: `${prefix}/guide/quick-start` },
        { text: t.groupCalls, link: `${prefix}/guide/group-calls` },
        { text: t.usingTheApp, link: `${prefix}/guide/using-the-app` },
        { text: t.troubleshooting, link: `${prefix}/guide/troubleshooting` },
      ],
    },
    {
      text: t.howItWorks,
      items: [
        { text: t.overview, link: `${prefix}/how-it-works/` },
        { text: t.signaling, link: `${prefix}/how-it-works/signaling` },
        { text: t.mediaState, link: `${prefix}/how-it-works/media-state` },
        { text: t.mediaSources, link: `${prefix}/how-it-works/media-sources` },
        { text: t.chat, link: `${prefix}/how-it-works/chat` },
        { text: t.pip, link: `${prefix}/how-it-works/picture-in-picture` },
        { text: t.e2ee, link: `${prefix}/how-it-works/e2ee` },
        { text: t.sfu, link: `${prefix}/how-it-works/group-calls` },
        { text: t.effects, link: `${prefix}/how-it-works/effects` },
      ],
    },
    {
      text: t.platforms,
      items: [
        { text: t.overview, link: `${prefix}/platforms/` },
        { text: 'Web', link: `${prefix}/platforms/web` },
        { text: 'Android', link: `${prefix}/platforms/android` },
        { text: 'iOS', link: `${prefix}/platforms/ios` },
        { text: 'macOS', link: `${prefix}/platforms/macos` },
        { text: 'Windows', link: `${prefix}/platforms/windows` },
      ],
    },
    {
      text: t.buildYourOwn,
      items: [
        { text: t.porting, link: `${prefix}/porting` },
        { text: t.customize, link: `${prefix}/customize` },
      ],
    },
    {
      text: t.reference,
      items: [
        { text: t.messages, link: `${prefix}/reference/messages` },
        { text: t.versions, link: `${prefix}/reference/versions` },
      ],
    },
  ]
}

function nav(prefix: string, t: Labels): DefaultTheme.NavItem[] {
  return [
    { text: t.guide, link: `${prefix}/guide/`, activeMatch: `^${prefix}/guide/` },
    { text: t.howItWorks, link: `${prefix}/how-it-works/`, activeMatch: `^${prefix}/how-it-works/` },
    { text: t.platforms, link: `${prefix}/platforms/`, activeMatch: `^${prefix}/platforms/` },
    { text: t.porting, link: `${prefix}/porting` },
    { text: t.reference, link: `${prefix}/reference/messages`, activeMatch: `^${prefix}/reference/` },
  ]
}

const en: Labels = {
  guide: 'Guide',
  whatIsThis: 'What is this?',
  quickStart: 'Quick start',
  groupCalls: 'Group calls',
  usingTheApp: 'Using the app',
  troubleshooting: 'Troubleshooting',
  howItWorks: 'How it works',
  overview: 'Overview',
  signaling: 'Signaling and call setup',
  mediaState: 'Camera and mic state',
  mediaSources: 'Switching video sources',
  chat: 'Chat',
  pip: 'Picture-in-picture',
  e2ee: 'End-to-end encryption',
  sfu: 'Group calls (SFU)',
  effects: 'Backgrounds and effects',
  platforms: 'Platforms',
  buildYourOwn: 'Build your own',
  porting: 'Port to a new platform',
  customize: 'Customize',
  reference: 'Reference',
  messages: 'Messages',
  versions: 'Versions and limitations',
}

const vi: Labels = {
  guide: 'Hướng dẫn',
  whatIsThis: 'Đây là gì?',
  quickStart: 'Bắt đầu nhanh',
  groupCalls: 'Gọi nhóm',
  usingTheApp: 'Dùng app',
  troubleshooting: 'Xử lý sự cố',
  howItWorks: 'Cách hoạt động',
  overview: 'Tổng quan',
  signaling: 'Signaling và thiết lập cuộc gọi',
  mediaState: 'Trạng thái camera và mic',
  mediaSources: 'Đổi nguồn video',
  chat: 'Chat',
  pip: 'Picture-in-picture',
  e2ee: 'Mã hóa đầu cuối',
  sfu: 'Gọi nhóm (SFU)',
  effects: 'Background và hiệu ứng',
  platforms: 'Nền tảng',
  buildYourOwn: 'Tự làm',
  porting: 'Làm cho nền tảng mới',
  customize: 'Tùy biến',
  reference: 'Tham khảo',
  messages: 'Message',
  versions: 'Phiên bản và giới hạn',
}

const zh: Labels = {
  guide: '指南',
  whatIsThis: '这是什么？',
  quickStart: '快速开始',
  groupCalls: '多人通话',
  usingTheApp: '使用应用',
  troubleshooting: '常见问题',
  howItWorks: '工作原理',
  overview: '概览',
  signaling: '信令与建立通话',
  mediaState: '摄像头和麦克风状态',
  mediaSources: '切换视频源',
  chat: '聊天',
  pip: '画中画',
  e2ee: '端到端加密',
  sfu: '多人通话（SFU）',
  effects: '背景与特效',
  platforms: '平台',
  buildYourOwn: '动手实现',
  porting: '移植到新平台',
  customize: '自定义',
  reference: '参考',
  messages: '消息',
  versions: '版本与限制',
}

const editPattern = `${repoUrl}/edit/master/docs/:path`

export default defineConfig({
  base,
  title: 'WebRTC Demo',
  cleanUrls: true,
  lastUpdated: true,
  head: [['link', { rel: 'icon', type: 'image/png', href: `${base}favicon.png` }]],
  markdown: {
    config(md) {
      md.use(repoLinks)
      md.use(anchorLinks)
      md.use(mermaidFences)
    },
  },
  vite: {
    // The E2EE demo imports the web client's own e2ee.ts.
    server: { fs: { allow: [repoRoot] } },
    // Mermaid is loaded on demand, in large chunks.
    build: { chunkSizeWarningLimit: 4000 },
  },
  themeConfig: {
    logo: '/logo.png',
    socialLinks: [{ icon: 'github', link: repoUrl }],
    search: {
      provider: 'local',
      options: {
        locales: {
          vi: {
            translations: {
              button: { buttonText: 'Tìm kiếm', buttonAriaLabel: 'Tìm kiếm' },
              modal: {
                noResultsText: 'Không tìm thấy kết quả cho',
                resetButtonTitle: 'Xóa',
                footer: { selectText: 'chọn', navigateText: 'di chuyển', closeText: 'đóng' },
              },
            },
          },
          zh: {
            translations: {
              button: { buttonText: '搜索', buttonAriaLabel: '搜索' },
              modal: {
                noResultsText: '没有找到相关结果',
                resetButtonTitle: '清除',
                footer: { selectText: '选择', navigateText: '切换', closeText: '关闭' },
              },
            },
          },
        },
      },
    },
  },
  locales: {
    root: {
      label: 'English',
      lang: 'en-US',
      description: 'One WebRTC call, five native apps: how the demo works and how to build your own client.',
      themeConfig: {
        nav: nav('', en),
        sidebar: sidebar('', en),
        editLink: { pattern: editPattern, text: 'Edit this page on GitHub' },
      },
    },
    vi: {
      label: 'Tiếng Việt',
      lang: 'vi-VN',
      description: 'Một cuộc gọi WebRTC, năm app native: demo hoạt động thế nào và cách tự làm một client.',
      themeConfig: {
        nav: nav('/vi', vi),
        sidebar: sidebar('/vi', vi),
        editLink: { pattern: editPattern, text: 'Sửa trang này trên GitHub' },
        outline: { label: 'Trong trang này' },
        docFooter: { prev: 'Trang trước', next: 'Trang sau' },
        lastUpdated: { text: 'Cập nhật lần cuối' },
        returnToTopLabel: 'Lên đầu trang',
        sidebarMenuLabel: 'Menu',
        darkModeSwitchLabel: 'Giao diện',
        lightModeSwitchTitle: 'Chuyển sang giao diện sáng',
        darkModeSwitchTitle: 'Chuyển sang giao diện tối',
        langMenuLabel: 'Đổi ngôn ngữ',
        notFound: {
          title: 'KHÔNG TÌM THẤY TRANG',
          quote: 'Trang này không tồn tại hoặc đã được chuyển đi.',
          linkText: 'Về trang chủ',
        },
      },
    },
    zh: {
      label: '简体中文',
      lang: 'zh-CN',
      description: '一次 WebRTC 通话，五个原生应用：这个 Demo 如何工作，以及如何实现自己的客户端。',
      themeConfig: {
        nav: nav('/zh', zh),
        sidebar: sidebar('/zh', zh),
        editLink: { pattern: editPattern, text: '在 GitHub 上编辑此页' },
        outline: { label: '本页内容' },
        docFooter: { prev: '上一页', next: '下一页' },
        lastUpdated: { text: '最后更新' },
        returnToTopLabel: '回到顶部',
        sidebarMenuLabel: '菜单',
        darkModeSwitchLabel: '外观',
        lightModeSwitchTitle: '切换到浅色模式',
        darkModeSwitchTitle: '切换到深色模式',
        langMenuLabel: '切换语言',
        notFound: {
          title: '页面未找到',
          quote: '这个页面不存在，或者已经被移走了。',
          linkText: '返回首页',
        },
      },
    },
  },
})
