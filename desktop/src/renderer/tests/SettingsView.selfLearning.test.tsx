import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { LocaleProvider } from '../contexts/LocaleContext'
import { SettingsView } from '../components/settings/SettingsView'
import { useConnectionStore } from '../stores/connectionStore'
import { usePendingRestartStore } from '../stores/pendingRestartStore'
import { useToastStore } from '../stores/toastStore'
import { useUIStore } from '../stores/uiStore'

const settingsGet = vi.fn()
const settingsSet = vi.fn()
const workspaceConfigGetCore = vi.fn()
const appServerSendRequest = vi.fn()
const appServerRestartManaged = vi.fn()
const proxyRestartManaged = vi.fn()

function PendingRestartHarness(): JSX.Element | null {
  const visible = usePendingRestartStore((s) => s.visible)
  const applying = usePendingRestartStore((s) => s.applying)
  const apply = usePendingRestartStore((s) => s.apply)
  const ignore = usePendingRestartStore((s) => s.ignore)
  if (!visible) return null
  return (
    <div role="status">
      <span>Changes require a service restart to take effect</span>
      <button type="button" onClick={() => ignore()} disabled={applying}>Ignore</button>
      <button type="button" onClick={() => void apply()} disabled={applying}>Apply & Restart</button>
    </div>
  )
}

function renderView(): void {
  render(
    <LocaleProvider>
      <PendingRestartHarness />
      <SettingsView workspacePath="E:\\Git\\dotcraft" />
    </LocaleProvider>
  )
}

describe('SettingsView self-learning settings', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    usePendingRestartStore.getState().clear()
    useToastStore.setState({ toasts: [] })
    useUIStore.getState().setShowThinkingContent(true)
    delete (window as Window & { __confirmDialog?: unknown }).__confirmDialog

    const core: any = {
      workspace: {
        apiKey: null,
        endPoint: null,
        welcomeSuggestionsEnabled: null,
        skillsSelfLearningEnabled: false,
        memoryAutoConsolidateEnabled: false,
        dreamsEnabled: null,
        dreamsInterval: null,
        dreamsThreadLookbackCount: null,
        dreamsAutoApply: null,
        defaultApprovalPolicy: 'default'
      },
      userDefaults: {
        apiKey: null,
        endPoint: null,
        welcomeSuggestionsEnabled: null,
        skillsSelfLearningEnabled: null,
        memoryAutoConsolidateEnabled: null,
        dreamsEnabled: null,
        dreamsInterval: null,
        dreamsThreadLookbackCount: null,
        dreamsAutoApply: null,
        defaultApprovalPolicy: null
      }
    }
    const dreamsStatus = {
      enabled: true,
      interval: '24:00:00',
      threadLookbackCount: 20,
      autoApply: false,
      historyTailChars: 20000,
      minCompletedTurnsSinceLastRun: 5,
      nextRunAt: null,
      running: false,
      activeDreamStoreId: null as string | null,
      lastRun: null as any
    }
    const dreamRuns: any[] = []

    settingsGet.mockResolvedValue({ locale: 'en', connectionMode: 'stdio', visibleChannels: [] })
    settingsSet.mockResolvedValue(undefined)
    workspaceConfigGetCore.mockImplementation(async () => core)
    appServerSendRequest.mockImplementation(async (method: string, params?: Record<string, unknown>) => {
      if (method === 'workspace/config/update') {
        if (typeof params?.defaultApprovalPolicy === 'string') {
          core.workspace.defaultApprovalPolicy = params.defaultApprovalPolicy
          return { defaultApprovalPolicy: core.workspace.defaultApprovalPolicy }
        }
        if (typeof params?.memoryAutoConsolidateEnabled === 'boolean') {
          core.workspace.memoryAutoConsolidateEnabled = params.memoryAutoConsolidateEnabled
          return { memoryAutoConsolidateEnabled: core.workspace.memoryAutoConsolidateEnabled }
        }
        if (typeof params?.dreamsEnabled === 'boolean') {
          core.workspace.dreamsEnabled = params.dreamsEnabled
          dreamsStatus.enabled = params.dreamsEnabled
          return { dreamsEnabled: core.workspace.dreamsEnabled }
        }
        if (typeof params?.dreamsInterval === 'string') {
          core.workspace.dreamsInterval = params.dreamsInterval
          dreamsStatus.interval = params.dreamsInterval
          return { dreamsInterval: core.workspace.dreamsInterval }
        }
        if (typeof params?.dreamsThreadLookbackCount === 'number') {
          core.workspace.dreamsThreadLookbackCount = params.dreamsThreadLookbackCount
          dreamsStatus.threadLookbackCount = params.dreamsThreadLookbackCount
          return { dreamsThreadLookbackCount: core.workspace.dreamsThreadLookbackCount }
        }
        if (typeof params?.dreamsAutoApply === 'boolean') {
          core.workspace.dreamsAutoApply = params.dreamsAutoApply
          dreamsStatus.autoApply = params.dreamsAutoApply
          return { dreamsAutoApply: core.workspace.dreamsAutoApply }
        }
        core.workspace.skillsSelfLearningEnabled = params?.skillsSelfLearningEnabled === true
        return { skillsSelfLearningEnabled: core.workspace.skillsSelfLearningEnabled }
      }
      if (method === 'dreams/status') {
        return { ...dreamsStatus }
      }
      if (method === 'dreams/run') {
        const run = {
          id: 'dream_20260511000000_test',
          status: 'succeeded',
          startedAt: '2026-05-11T00:00:00Z',
          endedAt: '2026-05-11T00:00:02Z',
          processedThreadCount: 2,
          candidateThreadCount: 2,
          dreamWritten: true,
          historyWritten: false,
          topicFilesWritten: 0,
          topicFilesDeleted: 0,
          evidenceSearchCount: 1,
          evidenceReadCount: 1,
          outputStoreId: 'store_20260511000000_test',
          reviewStatus: 'pending',
          autoApplied: false,
          errorType: null,
          evidenceThreadIds: ['thread-one'],
          writtenPaths: ['stores/store_20260511000000_test/INDEX.md'],
          threadId: 'thread_dream_fake',
          turnId: 'turn_dream_fake_2',
          turnIds: ['turn_dream_fake_1', 'turn_dream_fake_2'],
          trigger: 'manual',
          inputManifestPath: 'E:\\Git\\dotcraft\\.craft\\dreams\\runs\\dream_20260511000000_test\\input\\MANIFEST.md',
          message: null
        }
        dreamsStatus.lastRun = run
        dreamRuns.unshift(run)
        return { ...dreamsStatus }
      }
      if (method === 'dreams/list') {
        return { runs: [...dreamRuns] }
      }
      if (method === 'dreams/get') {
        const run = dreamRuns.find((item) => item.id === params?.runId) ?? null
        return {
          run,
          activeDreamStoreId: dreamsStatus.activeDreamStoreId,
          preview: run == null
            ? null
            : {
                activeStoreId: dreamsStatus.activeDreamStoreId,
                outputStoreId: run.outputStoreId,
                activeIndexMarkdown: dreamsStatus.activeDreamStoreId == null ? '' : '# Dream Store\n\n- Applied focus',
                outputIndexMarkdown: '# Dream Store\n\n- Pending focus',
                activeTopicPaths: [],
                outputTopicPaths: []
              }
        }
      }
      if (method === 'dreams/apply' || method === 'dreams/discard' || method === 'dreams/archive' || method === 'dreams/cancel') {
        const run = dreamRuns.find((item) => item.id === params?.runId) ?? null
        if (run != null) {
          if (method === 'dreams/apply') {
            run.reviewStatus = 'applied'
            dreamsStatus.activeDreamStoreId = run.outputStoreId
          } else if (method === 'dreams/discard') {
            run.reviewStatus = 'discarded'
          } else if (method === 'dreams/archive') {
            run.reviewStatus = 'archived'
          } else {
            run.status = 'canceled'
          }
          dreamsStatus.lastRun = run
        }
        return { run, activeDreamStoreId: dreamsStatus.activeDreamStoreId }
      }
      if (method === 'channel/list') {
        return { channels: [] }
      }
      return {}
    })
    appServerRestartManaged.mockResolvedValue(undefined)

    Object.defineProperty(window, 'api', {
      configurable: true,
      value: {
        settings: { get: settingsGet, set: settingsSet },
        workspaceConfig: { getCore: workspaceConfigGetCore },
        appServer: {
          sendRequest: appServerSendRequest,
          restartManaged: appServerRestartManaged,
          getResolvedBinary: vi.fn().mockResolvedValue({ path: null }),
          pickBinary: vi.fn()
        },
        proxy: {
          getResolvedBinary: vi.fn().mockResolvedValue({ path: null }),
          getStatus: vi.fn().mockResolvedValue({ status: 'stopped' }),
          listAuthFiles: vi.fn().mockResolvedValue([]),
          pickBinary: vi.fn(),
          restartManaged: proxyRestartManaged,
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
        shell: { openExternal: vi.fn() }
      }
    })

    useConnectionStore.getState().reset()
    useConnectionStore.setState({
      status: 'connected',
      capabilities: {
        workspaceConfigManagement: true,
        memoryManagement: true
      }
    })
  })

  it('saves self-learning toggle, shows global restart banner, and restarts managed AppServer', async () => {
    renderView()

    fireEvent.click(await screen.findByRole('button', { name: 'Personalization' }))
    const toggle = await screen.findByRole('switch', { name: 'Enable self-learning' })
    expect(toggle).toHaveAttribute('aria-checked', 'false')

    fireEvent.click(toggle)

    await waitFor(() => {
      expect(appServerSendRequest).toHaveBeenCalledWith('workspace/config/update', {
        skillsSelfLearningEnabled: true
      })
    })
    expect(await screen.findByText('Changes require a service restart to take effect')).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Apply & Restart' }))

    await waitFor(() => {
      expect(appServerRestartManaged).toHaveBeenCalledOnce()
    })
    await waitFor(() => {
      expect(screen.queryByText('Changes require a service restart to take effect')).not.toBeInTheDocument()
    })
  })

  it('groups personalization settings by conversation, learning, memory, and Dreams', async () => {
    useConnectionStore.setState({
      status: 'connected',
      capabilities: {
        workspaceConfigManagement: true,
        memoryManagement: true,
        dreams: true
      }
    })

    renderView()

    fireEvent.click(await screen.findByRole('button', { name: 'Personalization' }))

    expect(await screen.findByText('Customize workspace suggestions, learning, memory, and response display.')).toBeInTheDocument()
    expect(screen.getByText('Conversation')).toBeInTheDocument()
    expect(screen.getByText('Learning')).toBeInTheDocument()
    expect(screen.getByText('Memory')).toBeInTheDocument()
    expect(screen.getByText('Dreams')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Manage Dreams' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Run now' })).toBeInTheDocument()
  })

  it('defaults self-learning on when workspace and user defaults are unset', async () => {
    workspaceConfigGetCore.mockResolvedValueOnce({
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

    renderView()

    fireEvent.click(await screen.findByRole('button', { name: 'Personalization' }))
    const toggle = await screen.findByRole('switch', { name: 'Enable self-learning' })

    expect(toggle).toHaveAttribute('aria-checked', 'true')
  })

  it('defaults thinking content display on when the setting is absent', async () => {
    renderView()

    fireEvent.click(await screen.findByRole('button', { name: 'Personalization' }))
    const toggle = await screen.findByRole('switch', { name: 'Show thinking content' })

    expect(toggle).toHaveAttribute('aria-checked', 'true')
  })

  it('loads and saves the thinking content display preference', async () => {
    settingsGet.mockResolvedValueOnce({
      locale: 'en',
      connectionMode: 'stdio',
      visibleChannels: [],
      showThinkingContent: false
    })

    renderView()

    fireEvent.click(await screen.findByRole('button', { name: 'Personalization' }))
    const toggle = await screen.findByRole('switch', { name: 'Show thinking content' })
    expect(toggle).toHaveAttribute('aria-checked', 'false')

    fireEvent.click(toggle)

    await waitFor(() => {
      expect(settingsSet).toHaveBeenCalledWith({ showThinkingContent: true })
    })
    expect(useUIStore.getState().showThinkingContent).toBe(true)
  })

  it('saves long-term memory toggle without restart banner', async () => {
    renderView()

    fireEvent.click(await screen.findByRole('button', { name: 'Personalization' }))
    const toggle = await screen.findByRole('switch', { name: 'Enable long-term memory' })
    expect(toggle).toHaveAttribute('aria-checked', 'false')

    fireEvent.click(toggle)

    await waitFor(() => {
      expect(appServerSendRequest).toHaveBeenCalledWith('workspace/config/update', {
        memoryAutoConsolidateEnabled: true
      })
    })
    expect(screen.queryByText('Changes require a service restart to take effect')).not.toBeInTheDocument()
  })

  it('resets memory after confirmation and shows success toast', async () => {
    const confirm = vi.fn().mockResolvedValue(true)
    ;(window as Window & { __confirmDialog?: unknown }).__confirmDialog = confirm

    renderView()

    fireEvent.click(await screen.findByRole('button', { name: 'Personalization' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Reset' }))

    await waitFor(() => {
      expect(confirm).toHaveBeenCalledWith(expect.objectContaining({
        title: 'Reset memory?',
        danger: true
      }))
      expect(appServerSendRequest).toHaveBeenCalledWith('memory/reset', undefined, 20_000)
    })
    expect(useToastStore.getState().toasts).toEqual(
      expect.arrayContaining([
        expect.objectContaining({ message: 'Memory reset', type: 'success' })
      ])
    )
  })

  it('keeps memory reset hidden when the server capability is absent', async () => {
    useConnectionStore.setState({
      status: 'connected',
      capabilities: {
        workspaceConfigManagement: true
      }
    })

    renderView()

    fireEvent.click(await screen.findByRole('button', { name: 'Personalization' }))

    expect(screen.queryByText('Reset memory')).not.toBeInTheDocument()
  })

  it('keeps Dreams controls hidden when the server capability is absent', async () => {
    renderView()

    fireEvent.click(await screen.findByRole('button', { name: 'Personalization' }))

    expect(screen.queryByText('Enable Dreams')).not.toBeInTheDocument()
    expect(screen.queryByText('Dreams')).not.toBeInTheDocument()
    expect(appServerSendRequest).not.toHaveBeenCalledWith('dreams/status', undefined, 20_000)
  })

  it('loads Dreams status and saves Dreams settings', async () => {
    const confirm = vi.fn().mockResolvedValue(true)
    ;(window as Window & { __confirmDialog?: unknown }).__confirmDialog = confirm
    useConnectionStore.setState({
      status: 'connected',
      capabilities: {
        workspaceConfigManagement: true,
        memoryManagement: true,
        dreams: true
      }
    })

    renderView()

    fireEvent.click(await screen.findByRole('button', { name: 'Personalization' }))

    expect(await screen.findByRole('switch', { name: 'Enable Dreams' })).toHaveAttribute('aria-checked', 'true')
    expect(screen.getByRole('switch', { name: 'Auto-update Dreams' })).toHaveAttribute('aria-checked', 'false')
    await waitFor(() => {
      expect(appServerSendRequest).toHaveBeenCalledWith('dreams/status', undefined, 20_000)
    })

    fireEvent.click(screen.getByRole('switch', { name: 'Auto-update Dreams' }))
    fireEvent.change(screen.getByRole('combobox', { name: 'Dreams frequency' }), {
      target: { value: '12:00:00' }
    })
    fireEvent.change(screen.getByRole('combobox', { name: 'Recent threads' }), {
      target: { value: '50' }
    })

    await waitFor(() => {
      expect(confirm).toHaveBeenCalledWith(expect.objectContaining({
        title: 'Auto-update Dreams?',
        danger: true
      }))
      expect(appServerSendRequest).toHaveBeenCalledWith('workspace/config/update', {
        dreamsAutoApply: true
      })
      expect(appServerSendRequest).toHaveBeenCalledWith('workspace/config/update', {
        dreamsInterval: '12:00:00'
      })
      expect(appServerSendRequest).toHaveBeenCalledWith('workspace/config/update', {
        dreamsThreadLookbackCount: 50
      })
    })
  })

  it('runs Dreams now and reports completion', async () => {
    useConnectionStore.setState({
      status: 'connected',
      capabilities: {
        workspaceConfigManagement: true,
        memoryManagement: true,
        dreams: true
      }
    })

    renderView()

    fireEvent.click(await screen.findByRole('button', { name: 'Personalization' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Run now' }))

    await waitFor(() => {
      expect(appServerSendRequest).toHaveBeenCalledWith('dreams/run', undefined, 20_000)
      expect(useToastStore.getState().toasts).toEqual(
        expect.arrayContaining([
          expect.objectContaining({ message: 'Dreams run complete', type: 'success' })
        ])
      )
    })
  })

  it('opens Dreams management and sends run review to Dashboard', async () => {
    useConnectionStore.setState({
      status: 'connected',
      dashboardUrl: 'http://127.0.0.1:8080/dashboard',
      capabilities: {
        workspaceConfigManagement: true,
        memoryManagement: true,
        dreams: true
      }
    })

    renderView()

    fireEvent.click(await screen.findByRole('button', { name: 'Personalization' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Run now' }))

    await waitFor(() => {
      expect(appServerSendRequest).toHaveBeenCalledWith('dreams/run', undefined, 20_000)
    })

    fireEvent.click(screen.getByRole('button', { name: 'Manage Dreams' }))

    expect(await screen.findByText('dream_20260511000000_test')).toBeInTheDocument()
    fireEvent.click(await screen.findByRole('button', { name: 'Review' }))

    await waitFor(() => {
      expect(window.api.shell.openExternal).toHaveBeenCalledWith(
        'http://127.0.0.1:8080/dashboard#dreams/run/dream_20260511000000_test'
      )
    })
    expect(appServerSendRequest).not.toHaveBeenCalledWith('dreams/get', expect.anything(), expect.anything())
    expect(appServerSendRequest).not.toHaveBeenCalledWith('dreams/apply', expect.anything(), expect.anything())
  })

  it('shows memory reset failures in a toast', async () => {
    const confirm = vi.fn().mockResolvedValue(true)
    ;(window as Window & { __confirmDialog?: unknown }).__confirmDialog = confirm
    appServerSendRequest.mockImplementation(async (method: string) => {
      if (method === 'memory/reset') {
        throw new Error('disk denied')
      }
      if (method === 'channel/list') {
        return { channels: [] }
      }
      return {}
    })

    renderView()

    fireEvent.click(await screen.findByRole('button', { name: 'Personalization' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Reset' }))

    await waitFor(() => {
      expect(useToastStore.getState().toasts).toEqual(
        expect.arrayContaining([
          expect.objectContaining({
            message: 'Failed to reset memory: disk denied',
            type: 'error'
          })
        ])
      )
    })
  })

  it('shows restart banner for LLM edits and ignore only hides the banner', async () => {
    renderView()

    expect(await screen.findByRole('button', { name: 'LLM Service' })).toBeInTheDocument()
    expect(screen.queryByText('OpenAI API Service')).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'LLM Service' }))
    expect(await screen.findByText('OpenAI API Service')).toBeInTheDocument()
    const endpointInput = await screen.findByPlaceholderText('https://api.openai.com/v1') as HTMLInputElement
    fireEvent.change(endpointInput, { target: { value: 'https://models.example.test/v1' } })

    expect(await screen.findByText('Changes require a service restart to take effect')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Ignore' }))

    await waitFor(() => {
      expect(screen.queryByText('Changes require a service restart to take effect')).not.toBeInTheDocument()
    })
    expect(endpointInput.value).toBe('https://models.example.test/v1')
  })

  it('applies connection edits through the global restart banner', async () => {
    renderView()

    fireEvent.click(await screen.findByRole('button', { name: 'Connection' }))
    const modeSelect = await screen.findByRole('combobox', { name: 'Connection mode' }) as HTMLSelectElement
    fireEvent.change(modeSelect, { target: { value: 'remote' } })

    expect(await screen.findByText('Changes require a service restart to take effect')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Apply & Restart' }))

    await waitFor(() => {
      expect(settingsSet).toHaveBeenCalledWith(expect.objectContaining({
        connectionMode: 'remote'
      }))
      expect(appServerRestartManaged).toHaveBeenCalledOnce()
    })
  })

  it('applies API proxy edits through the global restart banner', async () => {
    renderView()

    fireEvent.click(await screen.findByText('API Proxy'))
    await screen.findByText('Enable local API proxy (CLIProxyAPI)')
    const proxyToggle = screen.getAllByRole('switch')[0]
    fireEvent.click(proxyToggle)

    expect(await screen.findByText('Changes require a service restart to take effect')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Apply & Restart' }))

    await waitFor(() => {
      expect(settingsSet).toHaveBeenCalledWith(expect.objectContaining({
        proxy: expect.objectContaining({ enabled: true })
      }))
      expect(proxyRestartManaged).toHaveBeenCalledOnce()
      expect(appServerRestartManaged).not.toHaveBeenCalled()
    })
  })

  it('restarts managed runtime when API proxy is disabled', async () => {
    settingsGet.mockResolvedValue({
      locale: 'en',
      connectionMode: 'stdio',
      visibleChannels: [],
      proxy: { enabled: true, port: 8317 }
    })

    renderView()

    fireEvent.click(await screen.findByText('API Proxy'))
    await screen.findByText('Enable local API proxy (CLIProxyAPI)')
    const proxyToggle = screen.getAllByRole('switch')[0]
    expect(proxyToggle).toHaveAttribute('aria-checked', 'true')
    fireEvent.click(proxyToggle)

    expect(await screen.findByText('Changes require a service restart to take effect')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Apply & Restart' }))

    await waitFor(() => {
      expect(settingsSet).toHaveBeenCalledWith(expect.objectContaining({
        proxy: expect.objectContaining({ enabled: false })
      }))
      expect(proxyRestartManaged).toHaveBeenCalledOnce()
      expect(appServerRestartManaged).not.toHaveBeenCalled()
    })
  })

  it('warns and saves full access default approval policy', async () => {
    const confirm = vi.fn().mockResolvedValue(true)
    ;(window as Window & { __confirmDialog?: unknown }).__confirmDialog = confirm

    renderView()

    const approvalSelect = await screen.findByRole('combobox', { name: 'Workspace default permissions' }) as HTMLSelectElement
    expect(approvalSelect.value).toBe('default')

    fireEvent.change(approvalSelect, { target: { value: 'autoApprove' } })

    await waitFor(() => {
      expect(confirm).toHaveBeenCalledWith(expect.objectContaining({ danger: true }))
      expect(appServerSendRequest).toHaveBeenCalledWith('workspace/config/update', {
        defaultApprovalPolicy: 'autoApprove'
      })
    })
  })

  it('keeps default approval policy when full access warning is cancelled', async () => {
    const confirm = vi.fn().mockResolvedValue(false)
    ;(window as Window & { __confirmDialog?: unknown }).__confirmDialog = confirm

    renderView()

    const approvalSelect = await screen.findByRole('combobox', { name: 'Workspace default permissions' }) as HTMLSelectElement
    expect(approvalSelect.value).toBe('default')

    fireEvent.change(approvalSelect, { target: { value: 'autoApprove' } })

    await waitFor(() => {
      expect(confirm).toHaveBeenCalledWith(expect.objectContaining({ danger: true }))
    })
    expect(appServerSendRequest).not.toHaveBeenCalledWith('workspace/config/update', {
      defaultApprovalPolicy: 'autoApprove'
    })
    expect(approvalSelect.value).toBe('default')
  })
})
