// @vitest-environment jsdom
import { beforeEach, describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { LocaleProvider } from '../contexts/LocaleContext'
import { PlanTab } from '../components/detail/PlanTab'
import { useConversationStore } from '../stores/conversationStore'

function renderPlanTab(): void {
  render(
    <LocaleProvider>
      <PlanTab />
    </LocaleProvider>
  )
}

describe('PlanTab', () => {
  beforeEach(() => {
    useConversationStore.getState().reset()
    Object.defineProperty(window, 'api', {
      configurable: true,
      value: {
        settings: {
          get: async () => ({ locale: 'en' })
        }
      }
    })
  })

  it('renders streaming CreatePlan as structured plan summary only', () => {
    const store = useConversationStore.getState()
    store.setTurns([
      {
        id: 'turn-1',
        threadId: 'thread-1',
        status: 'running',
        items: [],
        startedAt: new Date().toISOString()
      }
    ])

    store.onToolCallArgumentsDelta({
      turnId: 'turn-1',
      itemId: 'item-plan-1',
      delta: '{"plan":"# 实时计划\\n\\n## 概览\\n\\n正在写入计划正文。\\n\\n## 验证方案\\n\\n- 运行测试。","todos":[{"id":"verify","content":"Run tests","status":"pending"}]}',
      toolName: 'CreatePlan',
      callId: 'call-1'
    })

    renderPlanTab()

    expect(screen.getByText('Drafting plan…')).toBeInTheDocument()
    expect(screen.getByText('实时计划')).toBeInTheDocument()
    expect(screen.getAllByText('正在写入计划正文。').length).toBeGreaterThan(0)
    expect(screen.queryByText('验证方案')).toBeNull()
    expect(screen.queryByText('运行测试。')).toBeNull()
    expect(screen.getByText('Run tests')).toBeInTheDocument()
  })

  it('keeps the streaming placeholder until overview or todos are available', () => {
    const store = useConversationStore.getState()
    store.setTurns([
      {
        id: 'turn-1',
        threadId: 'thread-1',
        status: 'running',
        items: [],
        startedAt: new Date().toISOString()
      }
    ])

    store.onToolCallArgumentsDelta({
      turnId: 'turn-1',
      itemId: 'item-plan-2',
      delta: '{"plan":"# 实时计划\\n\\n## 概览',
      toolName: 'CreatePlan',
      callId: 'call-2'
    })

    renderPlanTab()

    expect(screen.getByText('Drafting plan…')).toBeInTheDocument()
    expect(screen.queryByText('实时计划')).toBeNull()
  })

  it('renders streaming todos even before overview is available', () => {
    const store = useConversationStore.getState()
    store.setTurns([
      {
        id: 'turn-1',
        threadId: 'thread-1',
        status: 'running',
        items: [],
        startedAt: new Date().toISOString()
      }
    ])

    store.onToolCallArgumentsDelta({
      turnId: 'turn-1',
      itemId: 'item-plan-3',
      delta: '{"plan":"# 实时计划","todos":[{"id":"verify","content":"Run tests","status":"pending"}]}',
      toolName: 'CreatePlan',
      callId: 'call-3'
    })

    renderPlanTab()

    expect(screen.getByText('Drafting plan…')).toBeInTheDocument()
    expect(screen.getByText('实时计划')).toBeInTheDocument()
    expect(screen.getByText('Run tests')).toBeInTheDocument()
  })
})
