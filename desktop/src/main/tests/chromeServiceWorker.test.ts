import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import vm from 'node:vm'
import { describe, expect, it } from 'vitest'

type ChromeTab = {
  id: number
  windowId: number
  index: number
  title?: string
  url?: string
  pendingUrl?: string
  status?: string
  active?: boolean
}

function createEvent() {
  return { addListener: () => undefined }
}

function loadServiceWorker(tabs: Map<number, ChromeTab>) {
  const updateCalls: Array<{ tabId: number; update: Record<string, unknown> }> = []
  const createCalls: Array<Record<string, unknown>> = []
  const context = {
    console,
    setTimeout,
    clearTimeout,
    Date,
    Promise,
    Error,
    String,
    Number,
    Boolean,
    RegExp,
    Array,
    Map,
    Set,
    JSON,
    chrome: {
      runtime: {
        connectNative: () => ({
          onMessage: createEvent(),
          onDisconnect: createEvent(),
          postMessage: () => undefined
        }),
        getManifest: () => ({ version: '0.0.0' }),
        lastError: null,
        onInstalled: createEvent(),
        onStartup: createEvent(),
        onMessage: createEvent()
      },
      action: {
        onClicked: createEvent()
      },
      tabs: {
        async get(tabId: number) {
          const tab = tabs.get(tabId)
          if (!tab) throw new Error(`Missing tab ${tabId}`)
          return { ...tab }
        },
        async update(tabId: number, update: Record<string, unknown>) {
          updateCalls.push({ tabId, update })
          const tab = tabs.get(tabId)
          if (!tab) throw new Error(`Missing tab ${tabId}`)
          const next = { ...tab, pendingUrl: String(update.url), status: 'loading', active: update.active === true }
          tabs.set(tabId, next)
          return { ...tab }
        },
        async create(options: Record<string, unknown>) {
          createCalls.push(options)
          const id = Math.max(0, ...tabs.keys()) + 1
          const tab = {
            id,
            windowId: 1,
            index: id - 1,
            title: '',
            url: options.url ? 'about:blank' : 'about:blank',
            pendingUrl: options.url ? String(options.url) : undefined,
            status: options.url ? 'loading' : 'complete',
            active: options.active !== false
          }
          tabs.set(id, tab)
          return { ...tab }
        },
        async query() {
          return [...tabs.values()].map((tab) => ({ ...tab }))
        },
        async remove(tabId: number) {
          tabs.delete(tabId)
        },
        async reload() {
          return undefined
        },
        async captureVisibleTab() {
          return 'data:image/png;base64,AQID'
        }
      },
      debugger: {
        async attach() {
          return undefined
        },
        async detach() {
          return undefined
        },
        async sendCommand() {
          return { result: { value: null } }
        }
      }
    }
  }
  vm.createContext(context)
  const serviceWorkerPath = path.resolve(
    path.dirname(fileURLToPath(import.meta.url)),
    '../../../../src/DotCraft.Core/Plugins/BuiltIn/chrome/extension/service_worker.js'
  )
  vm.runInContext(fs.readFileSync(serviceWorkerPath, 'utf8'), context, { filename: serviceWorkerPath })
  return {
    context: context as typeof context & {
      dispatchCommand: (method: string, params: Record<string, unknown>) => Promise<unknown>
    },
    updateCalls,
    createCalls
  }
}

describe('chrome extension service worker', () => {
  it('waits for tab.goto URL commit and returns fresh tab state', async () => {
    const tabs = new Map<number, ChromeTab>([[
      1,
      { id: 1, windowId: 1, index: 0, title: 'Extensions', url: 'chrome://extensions/', status: 'complete', active: true }
    ]])
    let getCount = 0
    const worker = loadServiceWorker(tabs)
    const originalGet = worker.context.chrome.tabs.get
    worker.context.chrome.tabs.get = async (tabId: number) => {
      getCount += 1
      if (getCount > 1) {
        tabs.set(tabId, {
          id: tabId,
          windowId: 1,
          index: 0,
          title: 'Bilibili',
          url: 'https://www.bilibili.com/',
          status: 'complete',
          active: true
        })
      }
      return originalGet(tabId)
    }

    const result = await worker.context.dispatchCommand('tab.goto', {
      tab: { id: 1 },
      url: 'https://www.bilibili.com/',
      options: { timeoutMs: 1000 }
    }) as Record<string, unknown>

    expect(worker.updateCalls).toEqual([
      { tabId: 1, update: { url: 'https://www.bilibili.com/', active: true } }
    ])
    expect(result).toMatchObject({
      id: 1,
      url: 'https://www.bilibili.com/',
      title: 'Bilibili',
      loading: false
    })
  })

  it('waits for tabs.new with a URL and honors waitUntil load', async () => {
    const tabs = new Map<number, ChromeTab>()
    const worker = loadServiceWorker(tabs)
    const originalGet = worker.context.chrome.tabs.get
    worker.context.chrome.tabs.get = async (tabId: number) => {
      const current = tabs.get(tabId)
      if (current?.pendingUrl) {
        tabs.set(tabId, {
          ...current,
          title: 'Example',
          url: current.pendingUrl,
          pendingUrl: undefined,
          status: 'complete'
        })
      }
      return originalGet(tabId)
    }

    const result = await worker.context.dispatchCommand('tabs.new', {
      url: 'https://example.test/',
      active: false,
      options: { waitUntil: 'load', timeoutMs: 1000 }
    }) as Record<string, unknown>

    expect(worker.createCalls).toEqual([
      { url: 'https://example.test/', active: false }
    ])
    expect(result).toMatchObject({
      url: 'https://example.test/',
      title: 'Example',
      loading: false
    })
  })

  it('reports the current URL when navigation commit times out', async () => {
    const tabs = new Map<number, ChromeTab>([[
      1,
      { id: 1, windowId: 1, index: 0, title: 'Extensions', url: 'chrome://extensions/', status: 'complete', active: true }
    ]])
    const worker = loadServiceWorker(tabs)

    await expect(worker.context.dispatchCommand('tab.waitForNavigation', {
      tab: { id: 1 },
      previousUrl: 'chrome://extensions/',
      options: { timeoutMs: 1 }
    })).rejects.toThrow('current URL is "chrome://extensions/"')
  })
})
