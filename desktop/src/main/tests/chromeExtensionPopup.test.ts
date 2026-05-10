import { readFile } from 'node:fs/promises'
import { afterEach, describe, expect, it, vi } from 'vitest'

async function importPopup() {
  const source = await readFile(
    new URL('../../../../src/DotCraft.Core/Plugins/BuiltIn/chrome/extension/popup.js', import.meta.url),
    'utf8'
  )
  return await import(`data:text/javascript;base64,${Buffer.from(source, 'utf8').toString('base64')}`)
}

function createRoot() {
  const classes = new Set<string>(['is-loading'])
  const pill = {
    classList: {
      add: (...names: string[]) => names.forEach((name) => classes.add(name)),
      remove: (...names: string[]) => names.forEach((name) => classes.delete(name))
    }
  }
  const label = { textContent: '' }
  const message = { textContent: '' }
  const version = { textContent: '' }
  const elements = new Map<string, unknown>([
    ['[data-status-pill]', pill],
    ['[data-status-label]', label],
    ['[data-status-message]', message],
    ['[data-version]', version]
  ])
  return {
    classes,
    label,
    message,
    version,
    root: {
      querySelector(selector: string) {
        return elements.get(selector) ?? null
      }
    }
  }
}

describe('Chrome extension popup', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    vi.resetModules()
  })

  it('renders connected and disconnected status view models', async () => {
    const { statusViewModel } = await importPopup()

    expect(statusViewModel({ connected: true, bridgeReady: true, version: '0.1.0' })).toMatchObject({
      label: 'Connected',
      className: 'is-connected',
      version: '0.1.0'
    })
    expect(statusViewModel({ connected: false, bridgeReady: false })).toMatchObject({
      label: 'Disconnected',
      className: 'is-disconnected'
    })
  })

  it('asks the service worker for status when refreshed', async () => {
    const sendMessage = vi.fn((_message, callback) => {
      callback({
        ok: true,
        status: { connected: true, bridgeReady: true, version: '0.1.0' }
      })
    })
    vi.stubGlobal('chrome', {
      runtime: {
        sendMessage,
        lastError: null,
        getManifest: () => ({ version: '0.1.0' })
      }
    })

    const { refreshStatus } = await importPopup()
    const view = createRoot()
    await refreshStatus(view.root)

    expect(sendMessage).toHaveBeenCalledWith({ type: 'dotcraft-popup-status' }, expect.any(Function))
    expect(view.classes.has('is-connected')).toBe(true)
    expect(view.label.textContent).toBe('Connected')
    expect(view.version.textContent).toBe('Version 0.1.0')
  })

  it('sends an open settings request through the service worker', async () => {
    const sendMessage = vi.fn((_message, callback) => {
      callback({
        ok: true,
        status: { connected: true, bridgeReady: true, version: '0.1.0' }
      })
    })
    vi.stubGlobal('chrome', {
      runtime: {
        sendMessage,
        lastError: null,
        getManifest: () => ({ version: '0.1.0' })
      }
    })

    const { openSettings } = await importPopup()
    await openSettings(createRoot().root)

    expect(sendMessage).toHaveBeenCalledWith({ type: 'dotcraft-popup-open-settings' }, expect.any(Function))
  })
})
