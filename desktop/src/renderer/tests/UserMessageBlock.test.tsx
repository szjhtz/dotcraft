import { describe, expect, it, vi, beforeEach } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { LocaleProvider } from '../contexts/LocaleContext'
import { UserMessageBlock } from '../components/conversation/UserMessageBlock'
import { GoalControlPopover } from '../components/conversation/GoalControlPopover'
import { useConversationStore } from '../stores/conversationStore'
import { useThreadStore } from '../stores/threadStore'
import { useUIStore } from '../stores/uiStore'
import { useViewerTabStore } from '../stores/viewerTabStore'

const settingsGet = vi.fn()
const readImageAsDataUrl = vi.fn()
const authorizeFile = vi.fn()
const classify = vi.fn()

function renderWithLocale(ui: JSX.Element): void {
  render(
    <LocaleProvider>
      {ui}
    </LocaleProvider>
  )
}

describe('UserMessageBlock trigger source pills', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    settingsGet.mockResolvedValue({ locale: 'en' })
    readImageAsDataUrl.mockResolvedValue({ dataUrl: '' })
    authorizeFile.mockImplementation(async ({ absolutePath }: { absolutePath: string }) => ({ absolutePath }))
    classify.mockResolvedValue({ contentClass: 'text', mime: 'text/plain', sizeBytes: 10 })
    useConversationStore.getState().reset()
    useConversationStore.setState({ workspacePath: 'F:/workspace' })
    useThreadStore.setState({ activeThreadId: 'thread-1' })
    useViewerTabStore.setState({
      byThread: new Map(),
      currentThreadId: 'thread-1',
      currentWorkspacePath: 'F:/workspace'
    })
    useUIStore.setState({
      activeDetailTab: { kind: 'system', id: 'changes' },
      detailPanelPreferredVisible: false,
      detailPanelVisible: false
    })
    Object.defineProperty(window, 'api', {
      configurable: true,
      value: {
        settings: { get: settingsGet },
        workspace: {
          readImageAsDataUrl,
          viewer: {
            authorizeFile,
            classify
          }
        },
        shell: { openExternal: vi.fn() }
      }
    })
  })

  it('renders goal continuation user messages with a goal source pill', () => {
    renderWithLocale(
      <UserMessageBlock
        text="Continue working toward the active thread goal"
        triggerKind="goal"
        triggerLabel="Goal continuation"
        triggerRefId="goal-1"
      />
    )

    expect(screen.getByText('Goal auto-continue')).toBeInTheDocument()
    expect(screen.getByTitle('Goal auto-continue · Goal continuation')).toBeInTheDocument()
  })

  it('keeps automation source pills visible for automation triggers', () => {
    renderWithLocale(
      <UserMessageBlock
        text="Run scheduled maintenance"
        triggerKind="automation"
        triggerLabel="Nightly checks"
        triggerRefId="task-1"
      />
    )

    expect(screen.getByRole('button', { name: 'Sent via automation · Automation · Nightly checks' })).toBeInTheDocument()
  })

  it('opens workspace-external file chips in the internal viewer', async () => {
    renderWithLocale(
      <UserMessageBlock
        text=""
        nativeInputParts={[
          { type: 'fileRef', path: 'C:\\temp\\notes.txt', displayPath: 'notes.txt' }
        ]}
      />
    )

    fireEvent.click(screen.getByRole('button', { name: 'Open notes.txt in DotCraft viewer' }))

    await waitFor(() => {
      expect(authorizeFile).toHaveBeenCalledWith({ absolutePath: 'C:/temp/notes.txt' })
      expect(classify).toHaveBeenCalledWith({ absolutePath: 'C:/temp/notes.txt' })
    })
    const activeTab = useUIStore.getState().activeDetailTab
    expect(activeTab.kind).toBe('viewer')
    if (activeTab.kind === 'viewer') {
      const tab = useViewerTabStore.getState().getThreadState('thread-1').tabs.find((entry) => entry.id === activeTab.id)
      expect(tab).toMatchObject({
        kind: 'file',
        absolutePath: 'C:/temp/notes.txt',
        relativePath: 'C:/temp/notes.txt',
        contentClass: 'text'
      })
    }
  })

  it('keeps failed external image rehydration clickable for the internal viewer', async () => {
    readImageAsDataUrl.mockRejectedValue(new Error('outside workspace'))
    authorizeFile.mockResolvedValue({ absolutePath: 'D:/pics/photo.png' })
    classify.mockResolvedValue({ contentClass: 'image', mime: 'image/png', sizeBytes: 20 })

    renderWithLocale(
      <UserMessageBlock
        text=""
        images={[{ path: 'D:/pics/photo.png', fileName: 'photo.png', mimeType: 'image/png' }]}
      />
    )

    const button = await screen.findByRole('button', { name: 'Open image photo.png in DotCraft viewer' })
    fireEvent.click(button)

    await waitFor(() => {
      expect(authorizeFile).toHaveBeenCalledWith({ absolutePath: 'D:/pics/photo.png' })
      expect(classify).toHaveBeenCalledWith({ absolutePath: 'D:/pics/photo.png' })
    })
    const activeTab = useUIStore.getState().activeDetailTab
    expect(activeTab.kind).toBe('viewer')
    if (activeTab.kind === 'viewer') {
      const tab = useViewerTabStore.getState().getThreadState('thread-1').tabs.find((entry) => entry.id === activeTab.id)
      expect(tab).toMatchObject({
        kind: 'file',
        absolutePath: 'D:/pics/photo.png',
        contentClass: 'image'
      })
    }
  })
})

describe('GoalControlPopover layout', () => {
  beforeEach(() => {
    settingsGet.mockResolvedValue({ locale: 'en' })
    Object.defineProperty(window, 'api', {
      configurable: true,
      value: {
        settings: { get: settingsGet }
      }
    })
  })

  it('centers the composer-adjacent goal editor popover', () => {
    renderWithLocale(
      <div style={{ position: 'relative' }}>
        <GoalControlPopover
          visible
          goal={null}
          onSetObjective={async () => true}
          onPause={async () => true}
          onResume={async () => true}
          onClear={async () => true}
          onDismiss={() => {}}
        />
      </div>
    )

    const dialog = screen.getByRole('dialog', { name: 'Goal' })
    const style = dialog.getAttribute('style') ?? ''
    expect(style).toContain('left: 50%')
    expect(style).toContain('transform: translateX(-50%)')
  })
})
