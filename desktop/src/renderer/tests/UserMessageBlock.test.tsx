import { describe, expect, it, vi, beforeEach } from 'vitest'
import { render, screen } from '@testing-library/react'
import { LocaleProvider } from '../contexts/LocaleContext'
import { UserMessageBlock } from '../components/conversation/UserMessageBlock'
import { GoalControlPopover } from '../components/conversation/GoalControlPopover'

const settingsGet = vi.fn()

function renderWithLocale(ui: JSX.Element): void {
  render(
    <LocaleProvider>
      {ui}
    </LocaleProvider>
  )
}

describe('UserMessageBlock trigger source pills', () => {
  beforeEach(() => {
    settingsGet.mockResolvedValue({ locale: 'en' })
    Object.defineProperty(window, 'api', {
      configurable: true,
      value: {
        settings: { get: settingsGet }
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
