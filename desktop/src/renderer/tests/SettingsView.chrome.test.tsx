import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { LocaleProvider } from '../contexts/LocaleContext'
import { SettingsView } from '../components/settings/SettingsView'
import { useConnectionStore } from '../stores/connectionStore'
import { usePluginStore, type PluginEntry } from '../stores/pluginStore'
import { useUIStore } from '../stores/uiStore'

const settingsGet = vi.fn()
const settingsSet = vi.fn()
const appServerSendRequest = vi.fn()
const chromeCheckSetup = vi.fn()
const chromeInstallNativeHost = vi.fn()
const chromeOpenChrome = vi.fn()

const browserPlugin: PluginEntry = {
  id: 'browser-use',
  displayName: 'Browser',
  description: 'Control the in-app browser with DotCraft',
  version: '1.0.0',
  enabled: false,
  installed: false,
  installable: true,
  removable: false,
  source: 'builtin',
  rootPath: '',
  interface: {
    displayName: 'Browser',
    shortDescription: 'Control the in-app browser with DotCraft',
    developerName: 'DotHarness',
    category: 'Coding'
  },
  functions: [],
  skills: [{ name: 'browser-use', description: 'Browser', enabled: false }],
  mcpServers: [],
  lspServers: []
}

const uninstalledChromePlugin: PluginEntry = {
  id: 'chrome',
  displayName: 'Chrome',
  description: 'Use your existing Chrome tabs and signed-in sites with DotCraft',
  version: '0.1.0',
  enabled: false,
  installed: false,
  installable: true,
  removable: false,
  source: 'builtin',
  rootPath: '',
  interface: {
    displayName: 'Chrome',
    shortDescription: 'Use your existing Chrome tabs and signed-in sites with DotCraft',
    developerName: 'DotHarness',
    category: 'Coding'
  },
  functions: [],
  skills: [{ name: 'chrome', description: 'Chrome', enabled: false }],
  mcpServers: [],
  lspServers: []
}

const installedChromePlugin: PluginEntry = {
  ...uninstalledChromePlugin,
  enabled: true,
  installed: true,
  installable: false,
  skills: [{ name: 'chrome', description: 'Chrome', enabled: true }]
}

function renderView(): void {
  render(
    <LocaleProvider>
      <SettingsView workspacePath="E:\\Git\\dotcraft" />
    </LocaleProvider>
  )
}

function installWindowApi(locale = 'en'): void {
  settingsGet.mockResolvedValue({ locale, connectionMode: 'local', visibleChannels: [] })
  settingsSet.mockResolvedValue(undefined)
  chromeCheckSetup.mockResolvedValue({
    extension: { ok: true, extensionId: 'pekajfcokkicggfjmickmkngmmoojlda', profile: 'Default' },
    nativeHost: { ok: true, manifestPath: 'host.json' },
    chromeRunning: { ok: true, processCount: 1 },
    installedBrowsers: { ok: true, browsers: [{ name: 'Google Chrome', executablePath: 'C:\\Chrome\\chrome.exe' }] },
    bridge: { ok: true }
  })
  chromeInstallNativeHost.mockResolvedValue({ ok: true, manifestPath: 'host.json' })
  chromeOpenChrome.mockResolvedValue({ ok: true })

  Object.defineProperty(window, 'api', {
    configurable: true,
    value: {
      settings: { get: settingsGet, set: settingsSet },
      workspaceConfig: {
        getCore: vi.fn().mockResolvedValue({
          workspace: {
            apiKey: null,
            endPoint: null,
            welcomeSuggestionsEnabled: null,
            skillsSelfLearningEnabled: null,
            memoryAutoConsolidateEnabled: null,
            defaultApprovalPolicy: null
          },
          userDefaults: {
            apiKey: null,
            endPoint: null,
            welcomeSuggestionsEnabled: null,
            skillsSelfLearningEnabled: null,
            memoryAutoConsolidateEnabled: null,
            defaultApprovalPolicy: null
          }
        })
      },
      appServer: {
        sendRequest: appServerSendRequest,
        restartManaged: vi.fn(),
        getResolvedBinary: vi.fn().mockResolvedValue({ path: null }),
        pickBinary: vi.fn()
      },
      proxy: {
        getResolvedBinary: vi.fn().mockResolvedValue({ path: null }),
        getStatus: vi.fn().mockResolvedValue({ status: 'stopped' }),
        listAuthFiles: vi.fn().mockResolvedValue([]),
        pickBinary: vi.fn(),
        restartManaged: vi.fn(),
        startOAuth: vi.fn(),
        getAuthStatus: vi.fn(),
        getUsageSummary: vi.fn().mockResolvedValue({
          totalRequests: 0,
          successCount: 0,
          failureCount: 0,
          totalTokens: 0,
          failedRequests: 0
        })
      },
      modules: { list: vi.fn().mockResolvedValue([]) },
      workspace: {
        pickFolder: vi.fn(),
        viewer: { browserUse: { clearCookies: vi.fn() } }
      },
      chrome: {
        checkSetup: chromeCheckSetup,
        installNativeHost: chromeInstallNativeHost,
        openChrome: chromeOpenChrome
      },
      shell: { openExternal: vi.fn() }
    }
  })
}

describe('SettingsView Chrome computer control', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    installWindowApi()
    appServerSendRequest.mockImplementation(async (method: string) => {
      if (method === 'channel/list') return { channels: [] }
      if (method === 'plugin/list') return { plugins: [browserPlugin, uninstalledChromePlugin], diagnostics: [] }
      if (method === 'skills/list') return { skills: [] }
      return {}
    })
    useConnectionStore.getState().reset()
    useConnectionStore.setState({
      status: 'connected',
      capabilities: {
        workspaceConfigManagement: true,
        pluginManagement: true
      }
    })
    usePluginStore.setState({
      plugins: [browserPlugin, uninstalledChromePlugin],
      diagnostics: [],
      loading: false,
      error: null,
      selectedPluginId: null,
      selectedPlugin: null,
      detailLoading: false
    })
    useUIStore.setState({ activeMainView: 'settings' })
  })

  it('renders Browser and Computer Control navigation labels', async () => {
    renderView()

    const browserNav = await screen.findByRole('button', { name: 'Browser' })
    expect(browserNav).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Computer Control' })).toBeInTheDocument()

    fireEvent.click(browserNav)
    expect(await screen.findByText("Manage DotCraft's browser.")).toBeInTheDocument()
  })

  it('renders the Chrome install shortcut when the plugin is not installed', async () => {
    renderView()

    fireEvent.click(await screen.findByRole('button', { name: 'Computer Control' }))

    expect(await screen.findByText('Control')).toBeInTheDocument()
    expect(screen.getByText('Chrome')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Install' })).toBeInTheDocument()
    expect(screen.queryByText('Always allowed apps')).not.toBeInTheDocument()
  })

  it('opens Chrome management details and runs setup checks', async () => {
    appServerSendRequest.mockImplementation(async (method: string) => {
      if (method === 'channel/list') return { channels: [] }
      if (method === 'plugin/list') return { plugins: [browserPlugin, installedChromePlugin], diagnostics: [] }
      if (method === 'skills/list') return { skills: [] }
      return {}
    })
    usePluginStore.setState({ plugins: [browserPlugin, installedChromePlugin] })

    renderView()
    fireEvent.click(await screen.findByRole('button', { name: 'Computer Control' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Manage' }))

    expect((await screen.findAllByText('Google Chrome')).length).toBeGreaterThan(0)
    await waitFor(() => expect(chromeCheckSetup).toHaveBeenCalled())
    expect(await screen.findByText('Connected')).toBeInTheDocument()
    expect(screen.getByText('DotCraft extension')).toBeInTheDocument()
    expect(screen.getByText('Chrome Bridge')).toBeInTheDocument()
    expect(screen.queryByText('Extension setup')).not.toBeInTheDocument()
    expect(screen.queryByText('C:\\Chrome\\chrome.exe')).not.toBeInTheDocument()
    expect(screen.queryByText('host.json')).not.toBeInTheDocument()
    expect(screen.queryByText('pekajfcokkicggfjmickmkngmmoojlda')).not.toBeInTheDocument()
    expect(screen.queryByText('Default')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Open extensions' })).not.toBeInTheDocument()
  })

  it('shows the Chrome extensions shortcut only when the extension needs attention', async () => {
    appServerSendRequest.mockImplementation(async (method: string) => {
      if (method === 'channel/list') return { channels: [] }
      if (method === 'plugin/list') return { plugins: [browserPlugin, installedChromePlugin], diagnostics: [] }
      if (method === 'skills/list') return { skills: [] }
      return {}
    })
    usePluginStore.setState({ plugins: [browserPlugin, installedChromePlugin] })
    chromeCheckSetup.mockResolvedValue({
      extension: { ok: false, extensionId: 'pekajfcokkicggfjmickmkngmmoojlda', profile: 'Default' },
      nativeHost: { ok: true, manifestPath: 'host.json' },
      chromeRunning: { ok: true, processCount: 1 },
      installedBrowsers: { ok: true, browsers: [{ name: 'Google Chrome', executablePath: 'C:\\Chrome\\chrome.exe' }] },
      bridge: { ok: true }
    })

    renderView()
    fireEvent.click(await screen.findByRole('button', { name: 'Computer Control' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Manage' }))

    const openExtensions = await screen.findByRole('button', { name: 'Open extensions' })
    fireEvent.click(openExtensions)

    await waitFor(() => expect(chromeOpenChrome).toHaveBeenCalledWith({
      url: 'chrome://extensions/?id=pekajfcokkicggfjmickmkngmmoojlda'
    }))
    expect(screen.queryByText('Extension setup')).not.toBeInTheDocument()
    expect(screen.queryByText('pekajfcokkicggfjmickmkngmmoojlda')).not.toBeInTheDocument()
    expect(screen.queryByText('C:\\Chrome\\chrome.exe')).not.toBeInTheDocument()
    expect(screen.queryByText('host.json')).not.toBeInTheDocument()
  })

  it('shows a disconnected status when the Chrome bridge is down', async () => {
    appServerSendRequest.mockImplementation(async (method: string) => {
      if (method === 'channel/list') return { channels: [] }
      if (method === 'plugin/list') return { plugins: [browserPlugin, installedChromePlugin], diagnostics: [] }
      if (method === 'skills/list') return { skills: [] }
      return {}
    })
    usePluginStore.setState({ plugins: [browserPlugin, installedChromePlugin] })
    chromeCheckSetup.mockResolvedValue({
      extension: { ok: true, extensionId: 'pekajfcokkicggfjmickmkngmmoojlda', profile: 'Default' },
      nativeHost: { ok: true, manifestPath: 'host.json' },
      chromeRunning: { ok: true, processCount: 1 },
      installedBrowsers: { ok: true, browsers: [{ name: 'Google Chrome', executablePath: 'C:\\Chrome\\chrome.exe' }] },
      bridge: { ok: false, error: 'Chrome bridge is not connected.' }
    })

    renderView()
    fireEvent.click(await screen.findByRole('button', { name: 'Computer Control' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Manage' }))

    expect(await screen.findByText('Disconnected')).toBeInTheDocument()
    expect(screen.getByText('Chrome Bridge')).toBeInTheDocument()
    expect(screen.queryByText('Chrome bridge is not connected.')).not.toBeInTheDocument()
  })

  it('reinstalls the Chrome native host from the detail action', async () => {
    appServerSendRequest.mockImplementation(async (method: string) => {
      if (method === 'channel/list') return { channels: [] }
      if (method === 'plugin/list') return { plugins: [browserPlugin, installedChromePlugin], diagnostics: [] }
      if (method === 'skills/list') return { skills: [] }
      return {}
    })
    usePluginStore.setState({ plugins: [browserPlugin, installedChromePlugin] })

    renderView()
    fireEvent.click(await screen.findByRole('button', { name: 'Computer Control' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Manage' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Install or repair Native Host' }))

    await waitFor(() => expect(chromeInstallNativeHost).toHaveBeenCalled())
    expect(chromeCheckSetup).toHaveBeenCalled()
  })
})
