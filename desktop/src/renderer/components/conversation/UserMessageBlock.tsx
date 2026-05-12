import { useEffect, useRef, useState } from 'react'
import { Bot, FileText, Image as ImageIcon, Pencil, Sparkle, Target, Terminal } from 'lucide-react'
import { useLocale, useT } from '../../contexts/LocaleContext'
import { translate } from '../../../shared/locales'
import { useConversationStore } from '../../stores/conversationStore'
import { useThreadStore } from '../../stores/threadStore'
import { useUIStore } from '../../stores/uiStore'
import { useCronStore } from '../../stores/cronStore'
import { ImageLightbox } from './ImageLightbox'
import { MessageCopyButton } from './MessageCopyButton'
import { parseUserMessageSegments, segmentsFromNativeInputParts } from './parseUserMessageSegments'
import type { ConversationItem, InputPart, UserMessageImageRef } from '../../types/conversation'
import { openConversationLink, openImagePathInViewer } from '../../utils/conversationDeepLink'
import { stripSystemReminderBlocks } from '../../utils/systemReminderText'
import { ActionTooltip } from '../ui/ActionTooltip'

const imageDataUrlCache = new Map<string, string>()

interface UserMessageBlockProps {
  text: string
  nativeInputParts?: InputPart[]
  imageDataUrls?: string[]
  images?: UserMessageImageRef[]
  createdAt?: string
  triggerKind?: ConversationItem['triggerKind']
  triggerLabel?: string
  triggerRefId?: string
  editable?: boolean
  onEdit?: () => void
  editing?: boolean
  editText?: string
  editSubmitting?: boolean
  editSubmitDisabled?: boolean
  onEditTextChange?: (text: string) => void
  onCancelEdit?: () => void
  onSubmitEdit?: () => void
}

/**
 * Renders a user message with a subtle background tint.
 * Plain text only — no Markdown. Spec §10.3.2
 * `@relative/path` tokens (from RichInputArea) render as compact file chips.
 */
export function UserMessageBlock({
  text,
  nativeInputParts,
  imageDataUrls,
  images,
  createdAt,
  triggerKind,
  triggerLabel,
  triggerRefId,
  editable = false,
  onEdit,
  editing = false,
  editText,
  editSubmitting = false,
  editSubmitDisabled = false,
  onEditTextChange,
  onCancelEdit,
  onSubmitEdit
}: UserMessageBlockProps): JSX.Element {
  const t = useT()
  const editAreaRef = useRef<HTMLTextAreaElement | null>(null)
  const [lightboxSrc, setLightboxSrc] = useState<string | null>(null)
  const [hovered, setHovered] = useState(false)
  const [focusedWithin, setFocusedWithin] = useState(false)
  const [hydratedImages, setHydratedImages] = useState<Array<{ url: string; absolutePath?: string }>>(
    (imageDataUrls ?? []).map((url) => ({ url }))
  )
  const [failedImages, setFailedImages] = useState<UserMessageImageRef[]>([])
  const workspacePath = useConversationStore((s) => s.workspacePath)
  const activeThreadId = useThreadStore((s) => s.activeThreadId)
  const hasImages = hydratedImages.length > 0
  const displayText = stripSystemReminderBlocks(text)
  const segments = nativeInputParts != null && nativeInputParts.length > 0
    ? segmentsFromNativeInputParts(nativeInputParts)
    : displayText.length > 0
      ? parseUserMessageSegments(displayText)
      : []
  const textSegments = segments
  const sentTime = formatMessageTime(createdAt)
  const actionsVisible = hovered || focusedWithin

  useEffect(() => {
    if (!editing) return
    const el = editAreaRef.current
    if (!el) return
    el.style.height = 'auto'
    const lineHeight = parseInt(getComputedStyle(el).lineHeight) || 20
    const maxHeight = lineHeight * 8 + 24
    el.style.height = `${Math.min(el.scrollHeight, maxHeight)}px`
    el.focus()
  }, [editing, editText])

  useEffect(() => {
    let cancelled = false

    const hydrateImages = async (): Promise<void> => {
      if (Array.isArray(imageDataUrls) && imageDataUrls.length > 0) {
        if (cancelled) return
        setHydratedImages(imageDataUrls.map((url) => ({ url })))
        setFailedImages([])
        return
      }
      if (!Array.isArray(images) || images.length === 0) {
        if (cancelled) return
        setHydratedImages([])
        setFailedImages([])
        return
      }

      const loaded: Array<{ url: string; absolutePath?: string }> = []
      const failed: UserMessageImageRef[] = []
      for (const image of images) {
        const cached = imageDataUrlCache.get(image.path)
        if (cached) {
          loaded.push({ url: cached, absolutePath: image.path })
          continue
        }
        try {
          const result = await window.api.workspace.readImageAsDataUrl({ path: image.path })
          const dataUrl = result.dataUrl
          if (dataUrl) {
            imageDataUrlCache.set(image.path, dataUrl)
            loaded.push({ url: dataUrl, absolutePath: image.path })
          } else {
            failed.push(image)
          }
        } catch {
          failed.push(image)
        }
      }
      if (cancelled) return
      setHydratedImages(loaded)
      setFailedImages(failed)
    }

    void hydrateImages()
    return () => {
      cancelled = true
    }
  }, [imageDataUrls, images])

  return (
    <>
      <div
        onMouseEnter={() => setHovered(true)}
        onMouseLeave={() => setHovered(false)}
        onFocusCapture={() => setFocusedWithin(true)}
        onBlurCapture={(e) => {
          if (!e.currentTarget.contains(e.relatedTarget)) {
            setFocusedWithin(false)
          }
        }}
        style={{
          alignSelf: 'flex-end',
          width: editing ? 'min(100%, var(--conversation-reading-width))' : undefined,
          maxWidth: editing
            ? 'var(--conversation-reading-width)'
            : 'min(82%, var(--conversation-reading-width))',
          display: 'flex',
          flexDirection: 'column',
          alignItems: 'flex-end'
        }}
      >
        <div
          style={{
            width: '100%',
            backgroundColor: 'var(--user-message-bg)',
            borderRadius: '12px',
            padding: '9px 13px',
            fontFamily: 'var(--font-body)',
            fontSize: 'var(--text-body-size)',
            lineHeight: 'var(--text-body-line-height)',
            color: 'var(--text-primary)',
            whiteSpace: 'pre-wrap',
            wordBreak: 'break-word',
            display: 'flex',
            flexDirection: 'column',
            gap: '6px',
            userSelect: 'text'
          }}
        >
          {editing ? (
            <>
              <textarea
                ref={editAreaRef}
                value={editText ?? displayText}
                aria-label={t('conversation.editTextarea')}
                disabled={editSubmitting}
                onChange={(e) => onEditTextChange?.(e.currentTarget.value)}
                onKeyDown={(e) => {
                  if (e.key === 'Escape') {
                    e.preventDefault()
                    onCancelEdit?.()
                    return
                  }
                  if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
                    e.preventDefault()
                    if (!editSubmitDisabled) {
                      onSubmitEdit?.()
                    }
                  }
                }}
                style={{
                  width: '100%',
                  minHeight: '72px',
                  maxHeight: '184px',
                  resize: 'none',
                  overflowY: 'auto',
                  border: 'none',
                  outline: 'none',
                  background: 'transparent',
                  color: 'var(--text-primary)',
                  font: 'inherit',
                  lineHeight: 'inherit',
                  padding: 0
                }}
              />
              <div
                style={{
                  display: 'flex',
                  justifyContent: 'flex-end',
                  alignItems: 'center',
                  gap: '8px'
                }}
              >
                <button
                  type="button"
                  onClick={onCancelEdit}
                  disabled={editSubmitting}
                  style={{
                    height: 32,
                    padding: '0 12px',
                    borderRadius: 16,
                    border: '1px solid var(--border-default)',
                    background: 'var(--bg-secondary)',
                    color: 'var(--text-secondary)',
                    cursor: editSubmitting ? 'default' : 'pointer',
                    opacity: editSubmitting ? 0.7 : 1
                  }}
                >
                  {t('common.cancel')}
                </button>
                <button
                  type="button"
                  onClick={onSubmitEdit}
                  disabled={editSubmitDisabled}
                  aria-label={t('conversation.editSend')}
                  style={{
                    height: 32,
                    padding: '0 14px',
                    borderRadius: 16,
                    border: '1px solid transparent',
                    background: editSubmitDisabled ? 'var(--bg-tertiary)' : 'var(--text-primary)',
                    color: editSubmitDisabled ? 'var(--text-dimmed)' : 'var(--bg-primary)',
                    cursor: editSubmitDisabled ? 'not-allowed' : 'pointer',
                    fontWeight: 600
                  }}
                >
                  {editSubmitting ? t('conversation.editSending') : t('conversation.editSend')}
                </button>
              </div>
            </>
          ) : (
            <>
          {(hasImages || failedImages.length > 0) && (
          <div
            style={{
              display: 'flex',
              flexDirection: 'row',
              flexWrap: 'wrap',
              gap: '8px'
            }}
          >
            {hydratedImages.map((imageItem, idx) => (
              <button
                key={`${idx}-${imageItem.url.slice(0, 32)}`}
                type="button"
                onClick={() => {
                  const fallbackToLightbox = (): void => {
                    setLightboxSrc(imageItem.url)
                  }
                  if (!imageItem.absolutePath || !workspacePath || !activeThreadId) {
                    fallbackToLightbox()
                    return
                  }
                  void openImagePathInViewer({
                    absolutePath: imageItem.absolutePath,
                    workspacePath,
                    threadId: activeThreadId,
                    t
                  }).then((opened) => {
                    if (!opened) fallbackToLightbox()
                  })
                }}
                style={{
                  padding: 0,
                  border: 'none',
                  background: 'transparent',
                  cursor: 'pointer',
                  borderRadius: '6px',
                  overflow: 'hidden',
                  lineHeight: 0
                }}
                aria-label={`View attached image ${idx + 1}`}
              >
                <img
                  src={imageItem.url}
                  alt=""
                  style={{
                    maxHeight: '80px',
                    maxWidth: '120px',
                    objectFit: 'cover',
                    display: 'block'
                  }}
                />
              </button>
            ))}
            {failedImages.map((imageItem, idx) => {
              const label = imageItem.fileName || basename(imageItem.path)
              return (
                <button
                  key={`failed-image-${imageItem.path}-${idx}`}
                  type="button"
                  onClick={() => {
                    if (!workspacePath || !activeThreadId) return
                    void openImagePathInViewer({
                      absolutePath: imageItem.path,
                      workspacePath,
                      threadId: activeThreadId,
                      t
                    })
                  }}
                  disabled={!workspacePath || !activeThreadId}
                  aria-label={t('conversation.openImageAttachmentAria', { file: label })}
                  title={imageItem.path}
                  style={{
                    display: 'inline-flex',
                    alignItems: 'center',
                    gap: '6px',
                    maxWidth: '180px',
                    padding: '6px 8px',
                    border: '1px solid var(--border-default)',
                    borderRadius: '6px',
                    background: 'var(--bg-secondary)',
                    color: 'var(--text-secondary)',
                    cursor: workspacePath && activeThreadId ? 'pointer' : 'default',
                    font: 'inherit',
                    fontSize: '12px',
                    lineHeight: 1.2
                  }}
                >
                  <ImageIcon size={14} strokeWidth={1.9} aria-hidden style={{ flexShrink: 0 }} />
                  <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                    {label}
                  </span>
                </button>
              )
            })}
          </div>
        )}
        {textSegments.length > 0 && (
          <span>
            {textSegments.map((seg, idx) =>
              seg.type === 'text' ? (
                <span key={`t-${idx}`}>{seg.value}</span>
              ) : seg.type === 'fileRef' ? (
                <FileRefChip
                  key={`f-${idx}-${seg.relativePath}`}
                  displayPath={seg.relativePath}
                  targetPath={seg.targetPath ?? seg.relativePath}
                  workspacePath={workspacePath}
                  activeThreadId={activeThreadId}
                />
              ) : seg.type === 'commandRef' ? (
                <CommandRefChip key={`c-${idx}-${seg.commandText}`} commandText={seg.commandText} />
              ) : (
                <SkillRefChip key={`s-${idx}-${seg.skillName}`} skillName={seg.skillName} />
              )
            )}
          </span>
        )}
        {triggerKind && (
          <TriggerSourcePill
            kind={triggerKind}
            label={triggerLabel}
            refId={triggerRefId}
          />
        )}
            </>
          )}
        </div>
        {!editing && (
          <div
            style={{
              minHeight: '24px',
              marginTop: '2px',
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'flex-end',
              gap: '6px',
              color: 'var(--text-tertiary)',
              fontSize: '11px',
              lineHeight: 1,
              userSelect: 'none'
            }}
          >
            {sentTime && (
              <span
                title={sentTime.title}
                style={{
                  padding: '0 2px',
                  opacity: actionsVisible ? 1 : 0,
                  transition: 'opacity 120ms ease'
                }}
              >
                {sentTime.label}
              </span>
            )}
            {editable && onEdit && (
              <ActionTooltip
                label={t('conversation.editMessage')}
                placement="top"
                wrapperStyle={{
                  display: 'inline-flex',
                  opacity: actionsVisible ? 1 : 0,
                  pointerEvents: actionsVisible ? 'auto' : 'none',
                  transition: 'opacity 120ms ease'
                }}
              >
                <button
                  type="button"
                  onClick={onEdit}
                  aria-label={t('conversation.editMessage')}
                  style={{
                    width: '24px',
                    height: '24px',
                    borderRadius: '6px',
                    border: '1px solid var(--border-default)',
                    background: 'var(--bg-secondary)',
                    color: 'var(--text-secondary)',
                    display: 'inline-flex',
                    alignItems: 'center',
                    justifyContent: 'center',
                    cursor: 'pointer',
                    transition: 'opacity 120ms ease, color 120ms ease'
                  }}
                >
                  <Pencil size={14} aria-hidden />
                </button>
              </ActionTooltip>
            )}
            <MessageCopyButton
              getText={() => displayText}
              visible={actionsVisible && displayText.length > 0}
              disabled={displayText.length === 0}
              wrapperStyle={{
                position: 'static',
                display: 'inline-flex',
                opacity: actionsVisible && displayText.length > 0 ? 1 : 0,
                pointerEvents: actionsVisible && displayText.length > 0 ? 'auto' : 'none',
                transition: 'opacity 120ms ease'
              }}
            />
          </div>
        )}
      </div>
      {lightboxSrc != null && (
        <ImageLightbox src={lightboxSrc} onClose={() => { setLightboxSrc(null) }} />
      )}
    </>
  )
}

function formatMessageTime(createdAt?: string): { label: string; title: string } | null {
  if (!createdAt) return null
  const date = new Date(createdAt)
  if (!Number.isFinite(date.getTime())) return null

  return {
    label: new Intl.DateTimeFormat(undefined, {
      hour: '2-digit',
      minute: '2-digit',
      hour12: false,
      hourCycle: 'h23'
    }).format(date),
    title: new Intl.DateTimeFormat(undefined, {
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
      hour12: false,
      hourCycle: 'h23'
    }).format(date)
  }
}

function SkillRefChip({ skillName }: { skillName: string }): JSX.Element {
  return (
    <span
      title={`$${skillName}`}
        style={{
          display: 'inline-flex',
          alignItems: 'center',
          gap: '4px',
          verticalAlign: 'baseline',
          margin: '0 2px',
          padding: '2px 8px',
          borderRadius: '999px',
          border: '1px solid color-mix(in srgb, var(--success) 38%, transparent)',
          background: 'color-mix(in srgb, var(--success) 16%, transparent)',
          color: 'var(--success)',
          fontSize: '12px',
          lineHeight: 1.25,
          whiteSpace: 'nowrap',
          userSelect: 'none',
          fontWeight: 600,
          maxWidth: 'var(--inline-reference-max-width)'
        }}
      >
      <Sparkle size={12} strokeWidth={2.25} aria-hidden />
      <span>{skillName}</span>
    </span>
  )
}

function CommandRefChip({ commandText }: { commandText: string }): JSX.Element {
  const label = commandText.startsWith('/') ? commandText.slice(1) : commandText
  return (
    <span
      title={commandText}
        style={{
          display: 'inline-flex',
          alignItems: 'center',
          gap: '4px',
          verticalAlign: 'baseline',
          margin: '0 2px',
          padding: '2px 8px',
          borderRadius: '999px',
          border: '1px solid color-mix(in srgb, var(--accent) 38%, transparent)',
          background: 'color-mix(in srgb, var(--accent) 16%, transparent)',
          color: 'var(--accent)',
          fontSize: '12px',
          lineHeight: 1.25,
          whiteSpace: 'nowrap',
          userSelect: 'none',
          fontWeight: 600,
          maxWidth: 'var(--inline-reference-max-width)'
        }}
      >
      <Terminal size={12} strokeWidth={2.25} aria-hidden />
      <span>{label}</span>
    </span>
  )
}

function FileRefChip({
  displayPath,
  targetPath,
  workspacePath,
  activeThreadId
}: {
  displayPath: string
  targetPath: string
  workspacePath: string
  activeThreadId: string | null
}): JSX.Element {
  const t = useT()
  const fileName = displayPath.split(/[/\\]/).pop() ?? displayPath
  const isAbsolutePath = /^[a-zA-Z]:[\\/]/.test(targetPath) || targetPath.startsWith('/') || targetPath.startsWith('\\\\')
  const title =
    workspacePath.length > 0 && !isAbsolutePath
      ? `${workspacePath.replace(/[/\\]+$/, '')}/${targetPath.replace(/^[/\\]+/, '')}`
      : targetPath
  const canOpen = workspacePath.length > 0 && !!activeThreadId

  return (
    <button
      type="button"
      title={title}
      aria-label={t('conversation.openFileRefAria', { file: fileName })}
      disabled={!canOpen}
      onClick={() => {
        if (!canOpen || !activeThreadId) return
        void openConversationLink({
          target: targetPath,
          workspacePath,
          threadId: activeThreadId,
          t
        })
      }}
        style={{
          display: 'inline-flex',
          alignItems: 'center',
          gap: '4px',
          verticalAlign: 'baseline',
          margin: '0 2px',
          padding: '2px 8px',
          borderRadius: '999px',
          border: '1px solid color-mix(in srgb, var(--border-active) 44%, transparent)',
          background: 'color-mix(in srgb, var(--bg-tertiary) 88%, transparent)',
          color: 'var(--text-primary)',
          fontSize: '12px',
          lineHeight: 1.25,
          whiteSpace: 'nowrap',
          userSelect: 'none',
          maxWidth: 'var(--inline-reference-max-width)',
          cursor: canOpen ? 'pointer' : 'default',
          font: 'inherit'
        }}
      >
      <FileText size={12} strokeWidth={2.1} aria-hidden />
      <span>{fileName}</span>
    </button>
  )
}

function basename(filePath: string): string {
  return filePath.split(/[\\/]/).pop() ?? filePath
}

function TriggerSourcePill({
  kind,
  label,
  refId
}: {
  kind: NonNullable<ConversationItem['triggerKind']>
  label?: string
  refId?: string
}): JSX.Element {
  const locale = useLocale()
  const setActiveMainView = useUIStore((s) => s.setActiveMainView)
  const setAutomationsTab = useUIStore((s) => s.setAutomationsTab)
  const selectCronJob = useCronStore((s) => s.selectCronJob)

  const canNavigate =
    (kind === 'cron' && !!refId) || (kind === 'automation' && !!refId)
  const isGoal = kind === 'goal'
  const badgeText = isGoal
    ? translate(locale, 'goal.triggeredBy.badge')
    : translate(locale, 'automation.triggeredBy.badge')
  const detailText = isGoal
    ? (label || translate(locale, 'goal.triggeredBy.generic'))
    : label
      ? translate(
          locale,
          kind === 'heartbeat'
            ? 'automation.triggeredBy.heartbeat'
            : kind === 'cron'
              ? 'automation.triggeredBy.cron'
              : 'automation.triggeredBy.task',
          { label }
        )
      : translate(locale, 'automation.triggeredBy.generic')

  const onClick = canNavigate
    ? () => {
        setActiveMainView('automations')
        if (kind === 'cron') {
          setAutomationsTab('cron')
          if (refId) selectCronJob(refId)
        } else {
          setAutomationsTab('tasks')
        }
      }
    : undefined

  const commonStyle = {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '4px',
    padding: '2px 8px',
    borderRadius: '999px',
    border: '1px solid color-mix(in srgb, var(--border-active) 36%, transparent)',
    background: 'color-mix(in srgb, var(--bg-tertiary) 80%, transparent)',
    color: 'var(--text-dimmed)',
    fontSize: '11px',
    lineHeight: 1.25,
    fontWeight: 500,
    alignSelf: 'flex-start',
    userSelect: 'none' as const
  }

  const title = `${badgeText} · ${detailText}`

  if (onClick) {
    return (
      <ActionTooltip label={title} wrapperStyle={{ display: 'inline-flex' }}>
        <button
          type="button"
          onClick={onClick}
          aria-label={title}
          style={{ ...commonStyle, cursor: 'pointer', border: commonStyle.border }}
        >
          <Bot size={11} strokeWidth={2.1} aria-hidden />
          <span>{badgeText}</span>
        </button>
      </ActionTooltip>
    )
  }

  return (
    <span title={title} style={commonStyle}>
      {isGoal ? (
        <Target size={11} strokeWidth={2.1} aria-hidden />
      ) : (
        <Bot size={11} strokeWidth={2.1} aria-hidden />
      )}
      <span>{badgeText}</span>
    </span>
  )
}
