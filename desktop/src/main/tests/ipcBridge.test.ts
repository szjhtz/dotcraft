import { describe, it, expect, vi, beforeEach } from 'vitest'
import { ipcMain, Notification, shell } from 'electron'
import { promises as fs } from 'fs'

const {
  scanModulesMock,
  moduleProcessManagerStartMock,
  detectEditorsMock,
  launchEditorMock,
  execFileMock,
  existsSyncMock,
  listWorkspaceFilesMock,
  notificationShowMock
} = vi.hoisted(() => ({
  scanModulesMock: vi.fn(),
  moduleProcessManagerStartMock: vi.fn(),
  detectEditorsMock: vi.fn(),
  launchEditorMock: vi.fn(),
  execFileMock: vi.fn(),
  existsSyncMock: vi.fn(),
  listWorkspaceFilesMock: vi.fn(),
  notificationShowMock: vi.fn()
}))

vi.mock('fs', () => ({
  existsSync: existsSyncMock,
  promises: {
    readFile: vi.fn(),
    writeFile: vi.fn(),
    stat: vi.fn(),
    mkdir: vi.fn(),
    rm: vi.fn(),
    rename: vi.fn()
  }
}))

vi.mock('child_process', () => ({
  execFile: execFileMock
}))

vi.mock('electron', () => {
  const NotificationMock = vi.fn(function (this: { show: () => void }, _options: unknown) {
    this.show = notificationShowMock
  })
  Object.assign(NotificationMock, {
    isSupported: vi.fn(() => false)
  })
  return {
  app: {
    isPackaged: true,
    getPath: vi.fn(() => 'C:\\Users\\tester'),
    getAppPath: vi.fn(() => 'F:\\dotcraft\\desktop')
  },
  ipcMain: {
    handle: vi.fn(),
    removeHandler: vi.fn()
  },
  BrowserWindow: {
    getAllWindows: vi.fn(() => []),
    getFocusedWindow: vi.fn(() => null)
  },
  dialog: {
    showOpenDialog: vi.fn()
  },
  Notification: NotificationMock,
  shell: {
    openExternal: vi.fn().mockResolvedValue(undefined),
    openPath: vi.fn().mockResolvedValue('')
  }
}})

vi.mock('../moduleScanner', async () => {
  const actual = await vi.importActual('../moduleScanner')
  return {
    ...actual,
    scanModules: scanModulesMock
  }
})

vi.mock('../moduleProcessManager', async () => {
  const actual = await vi.importActual('../moduleProcessManager')
  class MockModuleProcessManager {
    start = moduleProcessManagerStartMock
    stop = vi.fn()
    stopAll = vi.fn().mockResolvedValue(undefined)
    getStatusMap = vi.fn(() => ({}))
    autoStartModules = vi.fn().mockResolvedValue(undefined)
    getRecentLogs = vi.fn(() => [])
    getQrStatus = vi.fn(() => ({ active: false, qrDataUrl: null }))
  }
  return {
    ...actual,
    ModuleProcessManager: MockModuleProcessManager
  }
})

vi.mock('../externalEditors', () => ({
  detectEditors: detectEditorsMock,
  launchEditor: launchEditorMock
}))

vi.mock('../workspaceComposerIpc', async () => {
  const actual = await vi.importActual('../workspaceComposerIpc')
  return {
    ...actual,
    activateFileIndexWorkspace: vi.fn(),
    cleanupWorkspaceCache: vi.fn().mockResolvedValue(undefined),
    listWorkspaceFiles: listWorkspaceFilesMock,
    warmFileSearchIndex: vi.fn()
  }
})

import {
  createServerRequestBridge,
  registerIpcHandlers,
  unregisterIpcHandlers,
  sanitizeHttpOrHttpsUrl,
  sanitizeExternalUrl,
  openExternalUrl,
  openExternalHttpUrl,
  broadcastNotification,
  shouldShowTaskCompletionNotification
} from '../ipcBridge'

type IpcCallbacks = NonNullable<Parameters<typeof registerIpcHandlers>[3]>
type ExecFileCallback = (
  error: (Error & { code?: number | string }) | null,
  stdout: string,
  stderr: string
) => void

function createIpcCallbacks(overrides: Partial<IpcCallbacks> = {}): IpcCallbacks {
  return {
    onSwitchWorkspace: vi.fn().mockResolvedValue(undefined),
    onClearWorkspaceSelection: vi.fn().mockResolvedValue(undefined),
    onRunWorkspaceSetup: vi.fn().mockResolvedValue(undefined),
    onListSetupModels: vi.fn().mockResolvedValue({ kind: 'unsupported' }),
    onOpenNewWindow: vi.fn(),
    onRestartManagedAppServer: vi.fn().mockResolvedValue(undefined),
    onRestartManagedProxy: vi.fn().mockResolvedValue(undefined),
    getProxyStatus: vi.fn(() => ({ status: 'stopped' })),
    startProxyOAuth: vi.fn().mockResolvedValue({ url: 'http://127.0.0.1/oauth', state: 's1' }),
    getProxyOAuthStatus: vi.fn().mockResolvedValue({ status: 'wait' }),
    getProxyAuthFiles: vi.fn().mockResolvedValue([]),
    getProxyUsageSummary: vi.fn().mockResolvedValue({
      totalRequests: 0,
      successCount: 0,
      failureCount: 0,
      totalTokens: 0,
      failedRequests: 0
    }),
    getSettings: vi.fn(() => ({ locale: 'en' })),
    updateSettings: vi.fn(),
    getRecentWorkspaces: vi.fn(() => []),
    getConnectionStatus: vi.fn(() => ({ status: 'disconnected' })),
    getWorkspaceStatus: vi.fn(() => ({ status: 'ready', workspacePath: '/workspace', hasUserConfig: false })),
    ...overrides
  }
}

function registerHandlersForTest(workspacePath = '/workspace'): Map<string, (...args: unknown[]) => unknown> {
  const handlers = new Map<string, (...args: unknown[]) => unknown>()
  vi.mocked(ipcMain.handle).mockImplementation((channel, handler) => {
    handlers.set(channel, handler as (...args: unknown[]) => unknown)
  })
  registerIpcHandlers(null, () => null, workspacePath, createIpcCallbacks())
  return handlers
}

function gitError(exitCode: number, message = ''): Error & { code: number } {
  return Object.assign(new Error(message), { code: exitCode })
}

function mockGitCommands(
  resolver: (args: string[]) => { error?: Error & { code?: number | string }; stdout?: string; stderr?: string }
): void {
  execFileMock.mockImplementation((_command, args, _options, callback: ExecFileCallback) => {
    const result = resolver(args as string[])
    callback(result.error ?? null, result.stdout ?? '', result.stderr ?? '')
    return null
  })
}

// ---------------------------------------------------------------------------
// ipcBridge — server-request bridge tests
//
// The bridge creates a pending Promise per request (identified by bridgeId),
// which resolves when the Renderer sends back a response via
// appserver:server-response. These tests verify the pending-map logic directly
// (without standing up a real Electron IPC environment).
// ---------------------------------------------------------------------------

describe('createServerRequestBridge', () => {
  it('returns a unique bridgeId for each call', () => {
    const a = createServerRequestBridge()
    const b = createServerRequestBridge()
    expect(a.bridgeId).not.toBe(b.bridgeId)
  })

  it('returns a promise that is pending until resolved externally', async () => {
    const { promise } = createServerRequestBridge()
    let settled = false
    void promise.then(() => { settled = true })
    await new Promise((r) => setTimeout(r, 10))
    expect(settled).toBe(false)
  })

  it('bridge IDs are numeric strings in ascending order', () => {
    const ids = [
      createServerRequestBridge().bridgeId,
      createServerRequestBridge().bridgeId,
      createServerRequestBridge().bridgeId
    ]
    const nums = ids.map(Number)
    expect(nums[0]).toBeLessThan(nums[1])
    expect(nums[1]).toBeLessThan(nums[2])
  })
})

describe('sanitizeHttpOrHttpsUrl', () => {
  it('accepts http and https URLs and returns normalized href', () => {
    expect(sanitizeHttpOrHttpsUrl('http://127.0.0.1:8080/dashboard')).toBe(
      'http://127.0.0.1:8080/dashboard'
    )
    expect(sanitizeHttpOrHttpsUrl('https://example.com/path')).toBe('https://example.com/path')
  })

  it('returns null for empty, whitespace-only, or undefined', () => {
    expect(sanitizeHttpOrHttpsUrl(undefined)).toBeNull()
    expect(sanitizeHttpOrHttpsUrl('')).toBeNull()
    expect(sanitizeHttpOrHttpsUrl('   ')).toBeNull()
  })

  it('returns null for non-http(s) protocols', () => {
    expect(sanitizeHttpOrHttpsUrl('file:///etc/passwd')).toBeNull()
    expect(sanitizeHttpOrHttpsUrl('ms-msdt:foo')).toBeNull()
    expect(sanitizeHttpOrHttpsUrl('custom:host')).toBeNull()
  })

  it('returns null for malformed strings', () => {
    expect(sanitizeHttpOrHttpsUrl('not a url')).toBeNull()
  })
})

describe('openExternalHttpUrl', () => {
  it('throws Invalid URL for empty input', async () => {
    await expect(openExternalHttpUrl('')).rejects.toThrow('Invalid URL')
  })

  it('throws Only http(s) URLs are allowed for disallowed protocols', async () => {
    await expect(openExternalHttpUrl('file:///tmp/x')).rejects.toThrow('Only http(s) URLs are allowed')
  })

  it('calls shell.openExternal with sanitized href for https URL', async () => {
    vi.mocked(shell.openExternal).mockClear()
    await openExternalHttpUrl('https://example.com')
    expect(shell.openExternal).toHaveBeenCalledTimes(1)
    expect(shell.openExternal).toHaveBeenCalledWith('https://example.com/')
  })
})

describe('sanitizeExternalUrl', () => {
  it('accepts http(s), mailto, and tel URLs', () => {
    expect(sanitizeExternalUrl('https://example.com')).toBe('https://example.com/')
    expect(sanitizeExternalUrl('mailto:test@example.com')).toBe('mailto:test@example.com')
    expect(sanitizeExternalUrl('tel:+123456789')).toBe('tel:+123456789')
  })

  it('rejects unsupported schemes and overlong values', () => {
    expect(sanitizeExternalUrl('file:///tmp/x')).toBeNull()
    expect(sanitizeExternalUrl(`https://example.com/${'a'.repeat(5000)}`)).toBeNull()
  })
})

describe('openExternalUrl', () => {
  it('allows mailto URLs', async () => {
    vi.mocked(shell.openExternal).mockClear()
    await openExternalUrl('mailto:test@example.com')
    expect(shell.openExternal).toHaveBeenCalledWith('mailto:test@example.com')
  })

  it('rejects unsupported schemes', async () => {
    await expect(openExternalUrl('file:///tmp/x')).rejects.toThrow(
      'Only http(s), mailto, and tel URLs are allowed'
    )
  })
})

describe('registerIpcHandlers', () => {
  beforeEach(async () => {
    vi.clearAllMocks()
    scanModulesMock.mockResolvedValue([])
    moduleProcessManagerStartMock.mockResolvedValue({ ok: true })
    detectEditorsMock.mockResolvedValue([
      { id: 'cursor', labelKey: 'editors.cursor', iconKey: 'editor-generic' },
      { id: 'explorer', labelKey: 'editors.explorer', iconKey: 'explorer' }
    ])
    launchEditorMock.mockResolvedValue(undefined)
    listWorkspaceFilesMock.mockResolvedValue({
      files: [],
      indexStatus: 'ready',
      indexedCount: 0,
      stale: false
    })
    existsSyncMock.mockReturnValue(true)
  })

  it('git:commit filters missing and ignored paths before staging and committing', async () => {
    mockGitCommands((args) => {
      if (args[0] === 'status') {
        return { stdout: ' M src/valid.ts\0' }
      }
      if (args[0] === 'diff') {
        return { error: gitError(1) }
      }
      if (args[0] === 'commit') {
        return { stdout: '[main abc123] fix: valid\n 1 file changed\n' }
      }
      return { stdout: '' }
    })
    const handlers = registerHandlersForTest()
    const commit = handlers.get('git:commit')!

    const result = await commit(
      {},
      '/workspace',
      ['src/valid.ts', 'server/internal/distribution/db_migrations.go', 'ignored/generated.log'],
      'fix: valid'
    )

    expect(result).toBe('[main abc123] fix: valid\n 1 file changed')
    const gitCalls = execFileMock.mock.calls.map(([, args]) => args as string[])
    expect(gitCalls).toEqual([
      ['status', '--porcelain=v1', '-z', '--untracked-files=all', '--', 'src/valid.ts', 'server/internal/distribution/db_migrations.go', 'ignored/generated.log'],
      ['add', '--', 'src/valid.ts'],
      ['diff', '--cached', '--quiet', '--', 'src/valid.ts'],
      ['commit', '-m', 'fix: valid', '--', 'src/valid.ts']
    ])
  })

  it('git:commit skips add and commit when every requested path is filtered out', async () => {
    mockGitCommands((args) => {
      if (args[0] === 'status') return { stdout: '' }
      throw new Error(`Unexpected git command: ${args.join(' ')}`)
    })
    const handlers = registerHandlersForTest()
    const commit = handlers.get('git:commit')!

    await expect(
      commit({}, '/workspace', ['missing.txt', 'ignored/generated.log'], 'fix: nothing')
    ).rejects.toThrow('No Git changes to commit')

    const gitCalls = execFileMock.mock.calls.map(([, args]) => args as string[])
    expect(gitCalls).toEqual([
      ['status', '--porcelain=v1', '-z', '--untracked-files=all', '--', 'missing.txt', 'ignored/generated.log']
    ])
  })

  it('git:commit rejects paths outside the active workspace', async () => {
    const handlers = registerHandlersForTest()
    const commit = handlers.get('git:commit')!

    await expect(
      commit({}, '/workspace', ['/outside/secret.txt'], 'fix: outside')
    ).rejects.toThrow('Access denied')

    expect(execFileMock).not.toHaveBeenCalled()
  })

  it('git:commit rejects requests for a different workspace path', async () => {
    const handlers = registerHandlersForTest()
    const commit = handlers.get('git:commit')!

    await expect(
      commit({}, '/other-workspace', ['src/valid.ts'], 'fix: mismatch')
    ).rejects.toThrow('Workspace path mismatch')

    expect(execFileMock).not.toHaveBeenCalled()
  })

  it('git:commit constrains the commit pathspec to requested commit files', async () => {
    mockGitCommands((args) => {
      if (args[0] === 'status') {
        return { stdout: ' M src/valid.ts\0' }
      }
      if (args[0] === 'diff') {
        return { error: gitError(1) }
      }
      if (args[0] === 'commit') {
        return { stdout: '[main def456] fix: requested\n' }
      }
      return { stdout: '' }
    })
    const handlers = registerHandlersForTest()
    const commit = handlers.get('git:commit')!

    await commit({}, '/workspace', ['src/valid.ts'], 'fix: requested')

    const commitCall = execFileMock.mock.calls.find(([, args]) => (args as string[])[0] === 'commit')
    expect(commitCall?.[1]).toEqual(['commit', '-m', 'fix: requested', '--', 'src/valid.ts'])
    expect(commitCall?.[1]).not.toContain('src/already-staged.ts')
  })

  it('workspace:search-files returns empty when no workspace is active', async () => {
    const handlers = registerHandlersForTest('')
    const searchFiles = handlers.get('workspace:search-files')!

    const result = await searchFiles({}, { query: 'src', workspacePath: '', limit: 10 })

    expect(result).toEqual({
      files: [],
      indexStatus: 'empty',
      indexedCount: 0,
      stale: false
    })
    expect(listWorkspaceFilesMock).not.toHaveBeenCalled()
  })

  it('chrome:check-setup runs the fixed Chrome setup checks', async () => {
    execFileMock.mockImplementation((_command, args, _options, callback: ExecFileCallback) => {
      const script = String((args as string[])[0])
      if (script.endsWith('check-extension-installed.js')) callback(null, '{"ok":true,"extensionId":"abc"}', '')
      else if (script.endsWith('check-native-host-manifest.js')) callback(null, '{"ok":true,"manifestPath":"host.json"}', '')
      else if (script.endsWith('chrome-is-running.js')) callback(null, '{"ok":true,"processCount":1}', '')
      else if (script.endsWith('installed-browsers.js')) callback(null, '{"ok":true,"browsers":[]}', '')
      else callback(new Error(`Unexpected script: ${script}`), '', '')
      return null
    })
    const handlers = registerHandlersForTest()
    const checkSetup = handlers.get('chrome:check-setup')!
    const previousBridgePort = process.env.DOTCRAFT_CHROME_BRIDGE_PORT
    process.env.DOTCRAFT_CHROME_BRIDGE_PORT = '-1'

    const result = await checkSetup({})
    if (previousBridgePort == null) delete process.env.DOTCRAFT_CHROME_BRIDGE_PORT
    else process.env.DOTCRAFT_CHROME_BRIDGE_PORT = previousBridgePort

    expect(result).toEqual({
      extension: { ok: true, extensionId: 'abc' },
      nativeHost: { ok: true, manifestPath: 'host.json' },
      chromeRunning: { ok: true, processCount: 1 },
      installedBrowsers: { ok: true, browsers: [] },
      bridge: { ok: false, error: 'Invalid Chrome bridge port.' }
    })
    const scripts = execFileMock.mock.calls.map(([, args]) => String((args as string[])[0]).split(/[\\/]/).at(-1))
    expect(scripts).toEqual([
      'check-extension-installed.js',
      'check-native-host-manifest.js',
      'chrome-is-running.js',
      'installed-browsers.js'
    ])
    const [command, , options] = execFileMock.mock.calls[0]!
    expect(command).toBe(process.execPath)
    expect((options as { env?: NodeJS.ProcessEnv }).env?.ELECTRON_RUN_AS_NODE).toBe('1')
  })

  it('chrome:install-native-host runs only the bundled installer script', async () => {
    execFileMock.mockImplementation((_command, args, _options, callback: ExecFileCallback) => {
      callback(null, '{"ok":true,"manifestPath":"host.json"}', '')
      return null
    })
    const handlers = registerHandlersForTest()
    const installNativeHost = handlers.get('chrome:install-native-host')!

    const result = await installNativeHost({})

    expect(result).toEqual({ ok: true, manifestPath: 'host.json' })
    expect(execFileMock).toHaveBeenCalledOnce()
    const args = execFileMock.mock.calls[0]![1] as string[]
    expect(args[0]).toMatch(/installManifest\.mjs$/)
    expect(args.slice(1)).toEqual([])
  })

  it('chrome:open only forwards normalized Chrome launch targets', async () => {
    execFileMock.mockImplementation((_command, _args, _options, callback: ExecFileCallback) => {
      callback(null, '{"ok":true,"opened":true}', '')
      return null
    })
    const handlers = registerHandlersForTest()
    const openChrome = handlers.get('chrome:open')!

    await openChrome({}, { url: 'chrome://extensions/?id=abc' })
    await openChrome({}, { url: 'file:///C:/secret.txt' })

    expect(execFileMock).toHaveBeenCalledTimes(2)
    const firstArgs = execFileMock.mock.calls[0]![1] as string[]
    const secondArgs = execFileMock.mock.calls[1]![1] as string[]
    expect(firstArgs.at(-1)).toBe('chrome://extensions/?id=abc')
    expect(secondArgs.at(-1)).toBe('about:blank')
  })

  it('registers editors:list and returns detected editor entries', async () => {
    const handlers = new Map<string, (...args: unknown[]) => unknown>()
    vi.mocked(ipcMain.handle).mockImplementation((channel, handler) => {
      handlers.set(channel, handler as (...args: unknown[]) => unknown)
    })

    registerIpcHandlers(null, () => null, '/workspace', {
      onSwitchWorkspace: vi.fn().mockResolvedValue(undefined),
      onClearWorkspaceSelection: vi.fn().mockResolvedValue(undefined),
      onRunWorkspaceSetup: vi.fn().mockResolvedValue(undefined),
      onListSetupModels: vi.fn().mockResolvedValue({ kind: 'unsupported' }),
      onOpenNewWindow: vi.fn(),
      onRestartManagedAppServer: vi.fn().mockResolvedValue(undefined),
      onRestartManagedProxy: vi.fn().mockResolvedValue(undefined),
      getProxyStatus: vi.fn(() => ({ status: 'stopped' })),
      startProxyOAuth: vi.fn().mockResolvedValue({ url: 'http://127.0.0.1/oauth', state: 's1' }),
      getProxyOAuthStatus: vi.fn().mockResolvedValue({ status: 'wait' }),
      getProxyAuthFiles: vi.fn().mockResolvedValue([]),
      getProxyUsageSummary: vi.fn().mockResolvedValue({
        totalRequests: 0,
        successCount: 0,
        failureCount: 0,
        totalTokens: 0,
        failedRequests: 0
      }),
      getSettings: vi.fn(() => ({})),
      updateSettings: vi.fn(),
      getRecentWorkspaces: vi.fn(() => []),
      getConnectionStatus: vi.fn(() => ({ status: 'disconnected' })),
      getWorkspaceStatus: vi.fn(() => ({ status: 'no-workspace', workspacePath: '', hasUserConfig: false }))
    })

    const result = await handlers.get('editors:list')?.({})
    expect(detectEditorsMock).toHaveBeenCalledOnce()
    expect(result).toEqual([
      { id: 'cursor', labelKey: 'editors.cursor', iconKey: 'editor-generic' },
      { id: 'explorer', labelKey: 'editors.explorer', iconKey: 'explorer' }
    ])
  })

  it('registers editors:launch and validates workspace path before launch', async () => {
    const handlers = new Map<string, (...args: unknown[]) => unknown>()
    vi.mocked(ipcMain.handle).mockImplementation((channel, handler) => {
      handlers.set(channel, handler as (...args: unknown[]) => unknown)
    })

    registerIpcHandlers(null, () => null, '/workspace', {
      onSwitchWorkspace: vi.fn().mockResolvedValue(undefined),
      onClearWorkspaceSelection: vi.fn().mockResolvedValue(undefined),
      onRunWorkspaceSetup: vi.fn().mockResolvedValue(undefined),
      onListSetupModels: vi.fn().mockResolvedValue({ kind: 'unsupported' }),
      onOpenNewWindow: vi.fn(),
      onRestartManagedAppServer: vi.fn().mockResolvedValue(undefined),
      onRestartManagedProxy: vi.fn().mockResolvedValue(undefined),
      getProxyStatus: vi.fn(() => ({ status: 'stopped' })),
      startProxyOAuth: vi.fn().mockResolvedValue({ url: 'http://127.0.0.1/oauth', state: 's1' }),
      getProxyOAuthStatus: vi.fn().mockResolvedValue({ status: 'wait' }),
      getProxyAuthFiles: vi.fn().mockResolvedValue([]),
      getProxyUsageSummary: vi.fn().mockResolvedValue({
        totalRequests: 0,
        successCount: 0,
        failureCount: 0,
        totalTokens: 0,
        failedRequests: 0
      }),
      getSettings: vi.fn(() => ({ locale: 'en' })),
      updateSettings: vi.fn(),
      getRecentWorkspaces: vi.fn(() => []),
      getConnectionStatus: vi.fn(() => ({ status: 'disconnected' })),
      getWorkspaceStatus: vi.fn(() => ({ status: 'no-workspace', workspacePath: '', hasUserConfig: false }))
    })

    await handlers.get('editors:launch')?.({}, 'cursor', '/workspace')
    expect(launchEditorMock).toHaveBeenCalledWith('cursor', expect.stringMatching(/^[A-Z]:\\workspace$/))

    await expect(
      handlers.get('editors:launch')?.({}, 'cursor', '/outside')
    ).rejects.toThrow()
  })

  it('registers appserver:restart-managed and forwards to callback', async () => {
    const handlers = new Map<string, (...args: unknown[]) => unknown>()
    vi.mocked(ipcMain.handle).mockImplementation((channel, handler) => {
      handlers.set(channel, handler as (...args: unknown[]) => unknown)
    })

    const onRestartManagedAppServer = vi.fn().mockResolvedValue(undefined)
    const onListSetupModels = vi.fn().mockResolvedValue({ kind: 'unsupported' })

    registerIpcHandlers(null, () => null, '/workspace', {
      onSwitchWorkspace: vi.fn().mockResolvedValue(undefined),
      onClearWorkspaceSelection: vi.fn().mockResolvedValue(undefined),
      onRunWorkspaceSetup: vi.fn().mockResolvedValue(undefined),
      onListSetupModels,
      onOpenNewWindow: vi.fn(),
      onRestartManagedAppServer,
      onRestartManagedProxy: vi.fn().mockResolvedValue(undefined),
      getProxyStatus: vi.fn(() => ({ status: 'stopped' })),
      startProxyOAuth: vi.fn().mockResolvedValue({ url: 'http://127.0.0.1/oauth', state: 's1' }),
      getProxyOAuthStatus: vi.fn().mockResolvedValue({ status: 'wait' }),
      getProxyAuthFiles: vi.fn().mockResolvedValue([]),
      getProxyUsageSummary: vi.fn().mockResolvedValue({
        totalRequests: 0,
        successCount: 0,
        failureCount: 0,
        totalTokens: 0,
        failedRequests: 0
      }),
      getSettings: vi.fn(() => ({})),
      updateSettings: vi.fn(),
      getRecentWorkspaces: vi.fn(() => []),
      getConnectionStatus: vi.fn(() => ({ status: 'disconnected' })),
      getWorkspaceStatus: vi.fn(() => ({ status: 'no-workspace', workspacePath: '', hasUserConfig: false }))
    })

    expect(handlers.has('appserver:restart-managed')).toBe(true)
    await handlers.get('appserver:restart-managed')?.({})
    expect(onRestartManagedAppServer).toHaveBeenCalledOnce()
  })

  it('workspace-config:get-core reads nested Skills.SelfLearning.Enabled values', async () => {
    const handlers = new Map<string, (...args: unknown[]) => unknown>()
    vi.mocked(ipcMain.handle).mockImplementation((channel, handler) => {
      handlers.set(channel, handler as (...args: unknown[]) => unknown)
    })
    vi.mocked(fs.readFile).mockImplementation(async (filePath) => {
      const pathText = String(filePath)
      if (pathText.includes('dotcraft')) {
        return JSON.stringify({
          Memory: {
            AutoConsolidateEnabled: true
          },
          Skills: {
            SelfLearning: {
              Enabled: true
            }
          }
        })
      }
      return JSON.stringify({
        Memory: {
          AutoConsolidateEnabled: false
        },
        Skills: {
          SelfLearning: {
            Enabled: false
          }
        }
      })
    })

    registerIpcHandlers(null, () => null, '/workspace', {
      onSwitchWorkspace: vi.fn().mockResolvedValue(undefined),
      onClearWorkspaceSelection: vi.fn().mockResolvedValue(undefined),
      onRunWorkspaceSetup: vi.fn().mockResolvedValue(undefined),
      onListSetupModels: vi.fn().mockResolvedValue({ kind: 'unsupported' }),
      onOpenNewWindow: vi.fn(),
      onRestartManagedAppServer: vi.fn().mockResolvedValue(undefined),
      onRestartManagedProxy: vi.fn().mockResolvedValue(undefined),
      getProxyStatus: vi.fn(() => ({ status: 'stopped' })),
      startProxyOAuth: vi.fn().mockResolvedValue({ url: 'http://127.0.0.1/oauth', state: 's1' }),
      getProxyOAuthStatus: vi.fn().mockResolvedValue({ status: 'wait' }),
      getProxyAuthFiles: vi.fn().mockResolvedValue([]),
      getProxyUsageSummary: vi.fn().mockResolvedValue({
        totalRequests: 0,
        successCount: 0,
        failureCount: 0,
        totalTokens: 0,
        failedRequests: 0
      }),
      getSettings: vi.fn(() => ({})),
      updateSettings: vi.fn(),
      getRecentWorkspaces: vi.fn(() => []),
      getConnectionStatus: vi.fn(() => ({ status: 'disconnected' })),
      getWorkspaceStatus: vi.fn(() => ({
        status: 'ready',
        workspacePath: 'E:\\Git\\dotcraft',
        hasUserConfig: true
      }))
    })

    const result = await handlers.get('workspace-config:get-core')?.({})
    expect(result).toMatchObject({
      workspace: { skillsSelfLearningEnabled: true, memoryAutoConsolidateEnabled: true },
      userDefaults: { skillsSelfLearningEnabled: false, memoryAutoConsolidateEnabled: false }
    })
  })

  it('registers workspace:list-setup-models and forwards to callback', async () => {
    const handlers = new Map<string, (...args: unknown[]) => unknown>()
    vi.mocked(ipcMain.handle).mockImplementation((channel, handler) => {
      handlers.set(channel, handler as (...args: unknown[]) => unknown)
    })

    const onListSetupModels = vi.fn().mockResolvedValue({ kind: 'success', models: ['gpt-4.1'] })

    registerIpcHandlers(null, () => null, '/workspace', {
      onSwitchWorkspace: vi.fn().mockResolvedValue(undefined),
      onClearWorkspaceSelection: vi.fn().mockResolvedValue(undefined),
      onRunWorkspaceSetup: vi.fn().mockResolvedValue(undefined),
      onListSetupModels,
      onOpenNewWindow: vi.fn(),
      onRestartManagedAppServer: vi.fn().mockResolvedValue(undefined),
      onRestartManagedProxy: vi.fn().mockResolvedValue(undefined),
      getProxyStatus: vi.fn(() => ({ status: 'stopped' })),
      startProxyOAuth: vi.fn().mockResolvedValue({ url: 'http://127.0.0.1/oauth', state: 's1' }),
      getProxyOAuthStatus: vi.fn().mockResolvedValue({ status: 'wait' }),
      getProxyAuthFiles: vi.fn().mockResolvedValue([]),
      getProxyUsageSummary: vi.fn().mockResolvedValue({
        totalRequests: 0,
        successCount: 0,
        failureCount: 0,
        totalTokens: 0,
        failedRequests: 0
      }),
      getSettings: vi.fn(() => ({})),
      updateSettings: vi.fn(),
      getRecentWorkspaces: vi.fn(() => []),
      getConnectionStatus: vi.fn(() => ({ status: 'disconnected' })),
      getWorkspaceStatus: vi.fn(() => ({ status: 'no-workspace', workspacePath: '', hasUserConfig: false }))
    })

    expect(handlers.has('workspace:list-setup-models')).toBe(true)
    const result = await handlers.get('workspace:list-setup-models')?.({}, {
      endpoint: 'https://example.com/v1',
      apiKey: '',
      preferExistingUserConfig: true
    })
    expect(onListSetupModels).toHaveBeenCalledOnce()
    expect(result).toEqual({ kind: 'success', models: ['gpt-4.1'] })
  })

  it('registers proxy:list-auth-files and forwards to callback', async () => {
    const handlers = new Map<string, (...args: unknown[]) => unknown>()
    vi.mocked(ipcMain.handle).mockImplementation((channel, handler) => {
      handlers.set(channel, handler as (...args: unknown[]) => unknown)
    })

    const getProxyAuthFiles = vi.fn().mockResolvedValue([
      {
        provider: 'codex',
        status: 'ready',
        statusMessage: 'ok',
        disabled: false,
        unavailable: false,
        runtimeOnly: false,
        name: 'codex-user.json'
      }
    ])

    registerIpcHandlers(null, () => null, '/workspace', {
      onSwitchWorkspace: vi.fn().mockResolvedValue(undefined),
      onClearWorkspaceSelection: vi.fn().mockResolvedValue(undefined),
      onRunWorkspaceSetup: vi.fn().mockResolvedValue(undefined),
      onListSetupModels: vi.fn().mockResolvedValue({ kind: 'unsupported' }),
      onOpenNewWindow: vi.fn(),
      onRestartManagedAppServer: vi.fn().mockResolvedValue(undefined),
      onRestartManagedProxy: vi.fn().mockResolvedValue(undefined),
      getProxyStatus: vi.fn(() => ({ status: 'stopped' })),
      startProxyOAuth: vi.fn().mockResolvedValue({ url: 'http://127.0.0.1/oauth', state: 's1' }),
      getProxyOAuthStatus: vi.fn().mockResolvedValue({ status: 'wait' }),
      getProxyAuthFiles,
      getProxyUsageSummary: vi.fn().mockResolvedValue({
        totalRequests: 0,
        successCount: 0,
        failureCount: 0,
        totalTokens: 0,
        failedRequests: 0
      }),
      getSettings: vi.fn(() => ({})),
      updateSettings: vi.fn(),
      getRecentWorkspaces: vi.fn(() => []),
      getConnectionStatus: vi.fn(() => ({ status: 'disconnected' })),
      getWorkspaceStatus: vi.fn(() => ({ status: 'no-workspace', workspacePath: '', hasUserConfig: false }))
    })

    expect(handlers.has('proxy:list-auth-files')).toBe(true)
    const result = await handlers.get('proxy:list-auth-files')?.({})
    expect(getProxyAuthFiles).toHaveBeenCalledOnce()
    expect(result).toEqual([
      {
        provider: 'codex',
        status: 'ready',
        statusMessage: 'ok',
        disabled: false,
        unavailable: false,
        runtimeOnly: false,
        name: 'codex-user.json'
      }
    ])
  })

  it('rethrows invalid JSON from modules:read-config instead of returning an empty object', async () => {
    const handlers = new Map<string, (...args: unknown[]) => unknown>()
    vi.mocked(ipcMain.handle).mockImplementation((channel, handler) => {
      handlers.set(channel, handler as (...args: unknown[]) => unknown)
    })
    vi.mocked(fs.stat).mockResolvedValue({ size: 32 } as Awaited<ReturnType<typeof fs.stat>>)
    vi.mocked(fs.readFile).mockResolvedValue('{invalid-json' as Awaited<ReturnType<typeof fs.readFile>>)

    registerIpcHandlers(null, () => null, '/workspace', {
      onSwitchWorkspace: vi.fn().mockResolvedValue(undefined),
      onClearWorkspaceSelection: vi.fn().mockResolvedValue(undefined),
      onRunWorkspaceSetup: vi.fn().mockResolvedValue(undefined),
      onListSetupModels: vi.fn().mockResolvedValue({ kind: 'unsupported' }),
      onOpenNewWindow: vi.fn(),
      onRestartManagedAppServer: vi.fn().mockResolvedValue(undefined),
      onRestartManagedProxy: vi.fn().mockResolvedValue(undefined),
      getProxyStatus: vi.fn(() => ({ status: 'stopped' })),
      startProxyOAuth: vi.fn().mockResolvedValue({ url: 'http://127.0.0.1/oauth', state: 's1' }),
      getProxyOAuthStatus: vi.fn().mockResolvedValue({ status: 'wait' }),
      getProxyAuthFiles: vi.fn().mockResolvedValue([]),
      getProxyUsageSummary: vi.fn().mockResolvedValue({
        totalRequests: 0,
        successCount: 0,
        failureCount: 0,
        totalTokens: 0,
        failedRequests: 0
      }),
      getSettings: vi.fn(() => ({})),
      updateSettings: vi.fn(),
      getRecentWorkspaces: vi.fn(() => []),
      getConnectionStatus: vi.fn(() => ({ status: 'disconnected' })),
      getWorkspaceStatus: vi.fn(() => ({ status: 'no-workspace', workspacePath: '', hasUserConfig: false }))
    })

    await expect(
      handlers.get('modules:read-config')?.({}, { configFileName: 'module.json' })
    ).rejects.toThrow()
  })

  it('reads BOM-prefixed JSON in modules:read-config', async () => {
    const handlers = new Map<string, (...args: unknown[]) => unknown>()
    vi.mocked(ipcMain.handle).mockImplementation((channel, handler) => {
      handlers.set(channel, handler as (...args: unknown[]) => unknown)
    })
    vi.mocked(fs.stat).mockResolvedValue({ size: 32 } as Awaited<ReturnType<typeof fs.stat>>)
    vi.mocked(fs.readFile).mockResolvedValue('\uFEFF{"Enabled":true}' as Awaited<ReturnType<typeof fs.readFile>>)

    registerIpcHandlers(null, () => null, '/workspace', {
      onSwitchWorkspace: vi.fn().mockResolvedValue(undefined),
      onClearWorkspaceSelection: vi.fn().mockResolvedValue(undefined),
      onRunWorkspaceSetup: vi.fn().mockResolvedValue(undefined),
      onListSetupModels: vi.fn().mockResolvedValue({ kind: 'unsupported' }),
      onOpenNewWindow: vi.fn(),
      onRestartManagedAppServer: vi.fn().mockResolvedValue(undefined),
      onRestartManagedProxy: vi.fn().mockResolvedValue(undefined),
      getProxyStatus: vi.fn(() => ({ status: 'stopped' })),
      startProxyOAuth: vi.fn().mockResolvedValue({ url: 'http://127.0.0.1/oauth', state: 's1' }),
      getProxyOAuthStatus: vi.fn().mockResolvedValue({ status: 'wait' }),
      getProxyAuthFiles: vi.fn().mockResolvedValue([]),
      getProxyUsageSummary: vi.fn().mockResolvedValue({
        totalRequests: 0,
        successCount: 0,
        failureCount: 0,
        totalTokens: 0,
        failedRequests: 0
      }),
      getSettings: vi.fn(() => ({})),
      updateSettings: vi.fn(),
      getRecentWorkspaces: vi.fn(() => []),
      getConnectionStatus: vi.fn(() => ({ status: 'disconnected' })),
      getWorkspaceStatus: vi.fn(() => ({ status: 'no-workspace', workspacePath: '', hasUserConfig: false }))
    })

    await expect(
      handlers.get('modules:read-config')?.({}, { configFileName: 'module.json' })
    ).resolves.toEqual({
      exists: true,
      config: { Enabled: true }
    })
  })

  it('returns an error for invalid JSON in modules:start and does not overwrite the config file', async () => {
    const handlers = new Map<string, (...args: unknown[]) => unknown>()
    vi.mocked(ipcMain.handle).mockImplementation((channel, handler) => {
      handlers.set(channel, handler as (...args: unknown[]) => unknown)
    })
    scanModulesMock.mockResolvedValue([
      {
        moduleId: 'demo-module',
        channelName: 'demo',
        displayName: 'Demo',
        packageName: 'demo-module',
        configFileName: 'module.json',
        supportedTransports: ['stdio'],
        requiresInteractiveSetup: false,
        variant: 'default',
        source: 'user',
        absolutePath: '/workspace/modules/demo',
        configDescriptors: []
      }
    ])
    vi.mocked(fs.readFile).mockResolvedValue('{invalid-json' as Awaited<ReturnType<typeof fs.readFile>>)

    registerIpcHandlers(null, () => null, '/workspace', {
      onSwitchWorkspace: vi.fn().mockResolvedValue(undefined),
      onClearWorkspaceSelection: vi.fn().mockResolvedValue(undefined),
      onRunWorkspaceSetup: vi.fn().mockResolvedValue(undefined),
      onListSetupModels: vi.fn().mockResolvedValue({ kind: 'unsupported' }),
      onOpenNewWindow: vi.fn(),
      onRestartManagedAppServer: vi.fn().mockResolvedValue(undefined),
      onRestartManagedProxy: vi.fn().mockResolvedValue(undefined),
      getProxyStatus: vi.fn(() => ({ status: 'stopped' })),
      startProxyOAuth: vi.fn().mockResolvedValue({ url: 'http://127.0.0.1/oauth', state: 's1' }),
      getProxyOAuthStatus: vi.fn().mockResolvedValue({ status: 'wait' }),
      getProxyAuthFiles: vi.fn().mockResolvedValue([]),
      getProxyUsageSummary: vi.fn().mockResolvedValue({
        totalRequests: 0,
        successCount: 0,
        failureCount: 0,
        totalTokens: 0,
        failedRequests: 0
      }),
      getSettings: vi.fn(() => ({})),
      updateSettings: vi.fn(),
      getRecentWorkspaces: vi.fn(() => []),
      getConnectionStatus: vi.fn(() => ({ status: 'disconnected' })),
      getWorkspaceStatus: vi.fn(() => ({ status: 'no-workspace', workspacePath: '', hasUserConfig: false }))
    })

    await expect(
      handlers.get('modules:start')?.({}, { moduleId: 'demo-module' })
    ).resolves.toMatchObject({ ok: false })
    expect(vi.mocked(fs.writeFile)).not.toHaveBeenCalled()
    expect(moduleProcessManagerStartMock).not.toHaveBeenCalled()
  })

  it('returns an object-type error for non-object JSON in modules:start and does not overwrite the config file', async () => {
    const handlers = new Map<string, (...args: unknown[]) => unknown>()
    vi.mocked(ipcMain.handle).mockImplementation((channel, handler) => {
      handlers.set(channel, handler as (...args: unknown[]) => unknown)
    })
    scanModulesMock.mockResolvedValue([
      {
        moduleId: 'demo-module',
        channelName: 'demo',
        displayName: 'Demo',
        packageName: 'demo-module',
        configFileName: 'module.json',
        supportedTransports: ['stdio'],
        requiresInteractiveSetup: false,
        variant: 'default',
        source: 'user',
        absolutePath: '/workspace/modules/demo',
        configDescriptors: []
      }
    ])
    vi.mocked(fs.readFile).mockResolvedValue('["not-an-object"]' as Awaited<ReturnType<typeof fs.readFile>>)

    registerIpcHandlers(null, () => null, '/workspace', {
      onSwitchWorkspace: vi.fn().mockResolvedValue(undefined),
      onClearWorkspaceSelection: vi.fn().mockResolvedValue(undefined),
      onRunWorkspaceSetup: vi.fn().mockResolvedValue(undefined),
      onListSetupModels: vi.fn().mockResolvedValue({ kind: 'unsupported' }),
      onOpenNewWindow: vi.fn(),
      onRestartManagedAppServer: vi.fn().mockResolvedValue(undefined),
      onRestartManagedProxy: vi.fn().mockResolvedValue(undefined),
      getProxyStatus: vi.fn(() => ({ status: 'stopped' })),
      startProxyOAuth: vi.fn().mockResolvedValue({ url: 'http://127.0.0.1/oauth', state: 's1' }),
      getProxyOAuthStatus: vi.fn().mockResolvedValue({ status: 'wait' }),
      getProxyAuthFiles: vi.fn().mockResolvedValue([]),
      getProxyUsageSummary: vi.fn().mockResolvedValue({
        totalRequests: 0,
        successCount: 0,
        failureCount: 0,
        totalTokens: 0,
        failedRequests: 0
      }),
      getSettings: vi.fn(() => ({})),
      updateSettings: vi.fn(),
      getRecentWorkspaces: vi.fn(() => []),
      getConnectionStatus: vi.fn(() => ({ status: 'disconnected' })),
      getWorkspaceStatus: vi.fn(() => ({ status: 'no-workspace', workspacePath: '', hasUserConfig: false }))
    })

    await expect(
      handlers.get('modules:start')?.({}, { moduleId: 'demo-module' })
    ).resolves.toEqual({ ok: false, error: 'Config payload must be a JSON object' })
    expect(vi.mocked(fs.writeFile)).not.toHaveBeenCalled()
    expect(moduleProcessManagerStartMock).not.toHaveBeenCalled()
  })

  it('awaits async updateSettings in settings:set handler', async () => {
    const handlers = new Map<string, (...args: unknown[]) => unknown>()
    vi.mocked(ipcMain.handle).mockImplementation((channel, handler) => {
      handlers.set(channel, handler as (...args: unknown[]) => unknown)
    })

    let resolveUpdate: (() => void) | null = null
    const updateSettings = vi.fn(
      () =>
        new Promise<void>((resolve) => {
          resolveUpdate = resolve
        })
    )

    registerIpcHandlers(null, () => null, '/workspace', {
      onSwitchWorkspace: vi.fn().mockResolvedValue(undefined),
      onClearWorkspaceSelection: vi.fn().mockResolvedValue(undefined),
      onRunWorkspaceSetup: vi.fn().mockResolvedValue(undefined),
      onListSetupModels: vi.fn().mockResolvedValue({ kind: 'unsupported' }),
      onOpenNewWindow: vi.fn(),
      onRestartManagedAppServer: vi.fn().mockResolvedValue(undefined),
      onRestartManagedProxy: vi.fn().mockResolvedValue(undefined),
      getProxyStatus: vi.fn(() => ({ status: 'stopped' })),
      startProxyOAuth: vi.fn().mockResolvedValue({ url: 'http://127.0.0.1/oauth', state: 's1' }),
      getProxyOAuthStatus: vi.fn().mockResolvedValue({ status: 'wait' }),
      getProxyAuthFiles: vi.fn().mockResolvedValue([]),
      getProxyUsageSummary: vi.fn().mockResolvedValue({
        totalRequests: 0,
        successCount: 0,
        failureCount: 0,
        totalTokens: 0,
        failedRequests: 0
      }),
      getSettings: vi.fn(() => ({})),
      updateSettings,
      getRecentWorkspaces: vi.fn(() => []),
      getConnectionStatus: vi.fn(() => ({ status: 'disconnected' })),
      getWorkspaceStatus: vi.fn(() => ({ status: 'no-workspace', workspacePath: '', hasUserConfig: false }))
    })

    const settingsSet = handlers.get('settings:set')
    expect(settingsSet).toBeDefined()

    let settled = false
    const pending = Promise.resolve(settingsSet?.({}, { proxy: { enabled: false } })).then(() => {
      settled = true
    })

    await Promise.resolve()
    expect(updateSettings).toHaveBeenCalledOnce()
    expect(settled).toBe(false)

    resolveUpdate?.()
    await pending
    expect(settled).toBe(true)
  })
})

describe('task completion notifications', () => {
  function createWindow(focused: boolean): Electron.BrowserWindow {
    return {
      isDestroyed: vi.fn(() => false),
      isFocused: vi.fn(() => focused),
      webContents: {
        send: vi.fn()
      }
    } as unknown as Electron.BrowserWindow
  }

  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(Notification.isSupported).mockReturnValue(true)
  })

  it('defaults to showing task completion notifications only when unfocused', () => {
    expect(shouldShowTaskCompletionNotification(createWindow(false))).toBe(true)
    expect(shouldShowTaskCompletionNotification(createWindow(true))).toBe(false)
  })

  it('honors always and never task completion notification settings', () => {
    expect(shouldShowTaskCompletionNotification(createWindow(true), {
      notifications: { taskCompletionMode: 'always' }
    })).toBe(true)
    expect(shouldShowTaskCompletionNotification(createWindow(false), {
      notifications: { taskCompletionMode: 'never' }
    })).toBe(false)
  })

  it('shows native job result notifications according to settings while still forwarding renderer events', () => {
    const win = createWindow(true)

    broadcastNotification(win, 'system/jobResult', {
      jobName: 'Heartbeat',
      result: '**Done** with `task`'
    }, {
      notifications: { taskCompletionMode: 'always' }
    })

    expect(Notification).toHaveBeenCalledWith({
      title: 'Heartbeat',
      body: 'Done with task'
    })
    expect(notificationShowMock).toHaveBeenCalledOnce()
    expect(win.webContents.send).toHaveBeenCalledWith('appserver:notification', {
      method: 'system/jobResult',
      params: {
        jobName: 'Heartbeat',
        result: '**Done** with `task`'
      }
    })
  })

  it('suppresses native job result notifications when disabled but still forwards renderer events', () => {
    const win = createWindow(false)

    broadcastNotification(win, 'system/jobResult', {
      jobName: 'Cron',
      result: 'Done'
    }, {
      notifications: { taskCompletionMode: 'never' }
    })

    expect(Notification).not.toHaveBeenCalled()
    expect(notificationShowMock).not.toHaveBeenCalled()
    expect(win.webContents.send).toHaveBeenCalledWith('appserver:notification', {
      method: 'system/jobResult',
      params: {
        jobName: 'Cron',
        result: 'Done'
      }
    })
  })
})

describe('unregisterIpcHandlers', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('removes workspace-config:get-core and workspace:clear-recent handlers during teardown', () => {
    unregisterIpcHandlers()

    const removedChannels = vi.mocked(ipcMain.removeHandler).mock.calls.map(([channel]) => channel)
    expect(removedChannels).toContain('workspace-config:get-core')
    expect(removedChannels).toContain('workspace:clear-recent')
    expect(removedChannels.filter((channel) => channel === 'workspace-config:get-core')).toHaveLength(1)
    expect(removedChannels.filter((channel) => channel === 'workspace:clear-recent')).toHaveLength(1)
  })

  it('removes the new workspace handlers after they are registered', () => {
    registerIpcHandlers(null, () => null, '/workspace', {
      onSwitchWorkspace: vi.fn().mockResolvedValue(undefined),
      onClearWorkspaceSelection: vi.fn().mockResolvedValue(undefined),
      onRunWorkspaceSetup: vi.fn().mockResolvedValue(undefined),
      onListSetupModels: vi.fn().mockResolvedValue({ kind: 'unsupported' }),
      onOpenNewWindow: vi.fn(),
      onRestartManagedAppServer: vi.fn().mockResolvedValue(undefined),
      onRestartManagedProxy: vi.fn().mockResolvedValue(undefined),
      getProxyStatus: vi.fn(() => ({ status: 'stopped' })),
      startProxyOAuth: vi.fn().mockResolvedValue({ url: 'http://127.0.0.1/oauth', state: 's1' }),
      getProxyOAuthStatus: vi.fn().mockResolvedValue({ status: 'wait' }),
      getProxyAuthFiles: vi.fn().mockResolvedValue([]),
      getProxyUsageSummary: vi.fn().mockResolvedValue({
        totalRequests: 0,
        successCount: 0,
        failureCount: 0,
        totalTokens: 0,
        failedRequests: 0
      }),
      getSettings: vi.fn(() => ({})),
      updateSettings: vi.fn(),
      getRecentWorkspaces: vi.fn(() => []),
      clearRecentWorkspaces: vi.fn(),
      getConnectionStatus: vi.fn(() => ({ status: 'disconnected' })),
      getWorkspaceStatus: vi.fn(() => ({ status: 'no-workspace', workspacePath: '', hasUserConfig: false }))
    })

    vi.mocked(ipcMain.removeHandler).mockClear()

    unregisterIpcHandlers()

    expect(ipcMain.removeHandler).toHaveBeenCalledWith('workspace-config:get-core')
    expect(ipcMain.removeHandler).toHaveBeenCalledWith('workspace:clear-recent')
  })
})

// ---------------------------------------------------------------------------
// WireProtocolClient — bidirectional request routing
// (covered in WireProtocolClient.test.ts, but verified here as integration)
// ---------------------------------------------------------------------------

import { Readable, Writable, PassThrough } from 'stream'
import { WireProtocolClient } from '../WireProtocolClient'

describe('WireProtocolClient bidirectional routing', () => {
  it('server request handler result is sent back as JSON-RPC response with original id', async () => {
    const toServer = new PassThrough()
    const fromServer = new PassThrough()
    const client = new WireProtocolClient(
      fromServer as unknown as Readable,
      toServer as unknown as Writable
    )

    const responseLines: string[] = []
    toServer.on('data', (chunk: Buffer) => {
      chunk.toString('utf8').split('\n').filter(Boolean).forEach((l) => responseLines.push(l))
    })

    // Register a handler that simulates the approval bridge: returns the decision
    client.onServerRequest(async (_method, params) => {
      const p = params as Record<string, unknown>
      return { decision: p.defaultDecision ?? 'accept' }
    })

    // AppServer sends a server-initiated request
    fromServer.push(
      JSON.stringify({
        jsonrpc: '2.0',
        id: 42,
        method: 'item/approval/request',
        params: { approvalType: 'shell', operation: 'rm -rf /tmp', defaultDecision: 'decline' }
      }) + '\n'
    )

    await new Promise((r) => setTimeout(r, 20))

    // Filter out any initialize or other requests from the response lines
    const approvalResponse = responseLines
      .map((l) => JSON.parse(l))
      .find((m) => m.id === 42 && 'result' in m)

    expect(approvalResponse).toBeDefined()
    expect(approvalResponse).toMatchObject({
      jsonrpc: '2.0',
      id: 42,
      result: { decision: 'decline' }
    })

    client.dispose()
    toServer.destroy()
    fromServer.destroy()
  })

  it('sends an error response when handler throws', async () => {
    const toServer = new PassThrough()
    const fromServer = new PassThrough()
    const client = new WireProtocolClient(
      fromServer as unknown as Readable,
      toServer as unknown as Writable
    )

    const responseLines: string[] = []
    toServer.on('data', (chunk: Buffer) => {
      chunk.toString('utf8').split('\n').filter(Boolean).forEach((l) => responseLines.push(l))
    })

    client.onServerRequest(async () => {
      throw new Error('Bridge unavailable')
    })

    fromServer.push(
      JSON.stringify({ jsonrpc: '2.0', id: 77, method: 'item/approval/request', params: {} }) + '\n'
    )

    await new Promise((r) => setTimeout(r, 20))

    const errorResponse = responseLines
      .map((l) => JSON.parse(l))
      .find((m) => m.id === 77 && 'error' in m)

    expect(errorResponse).toBeDefined()
    expect(errorResponse.error.code).toBe(-32603)

    client.dispose()
    toServer.destroy()
    fromServer.destroy()
  })
})
