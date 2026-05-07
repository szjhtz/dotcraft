import { afterEach, describe, expect, it } from 'vitest'
import { mkdir, mkdtemp, rm, writeFile } from 'fs/promises'
import { existsSync } from 'fs'
import { tmpdir } from 'os'
import { join } from 'path'
import { cleanupWorkspaceCache } from '../workspaceComposerIpc'

describe('workspace composer cache cleanup', () => {
  let tempRoot = ''

  afterEach(async () => {
    if (tempRoot) await rm(tempRoot, { recursive: true, force: true })
    tempRoot = ''
  })

  async function createCacheDir(): Promise<string> {
    tempRoot = await mkdtemp(join(tmpdir(), 'dotcraft-workspace-cache-'))
    const cacheDir = join(tempRoot, '.craft', 'cache')
    await mkdir(cacheDir, { recursive: true })
    return cacheDir
  }

  const pascalCaseWelcomeSuggestionsCache = JSON.stringify({
    SchemaVersion: 1,
    Result: {
      Source: 'dynamic',
      Fingerprint: 'abc123',
      GeneratedAt: '2026-05-06T00:00:00.0000000+00:00',
      Items: [
        {
          Title: 'Review cache cleanup',
          Prompt: 'Review Desktop welcome suggestion cache cleanup.'
        }
      ]
    }
  })

  it('removes invalid Desktop-owned file index caches', async () => {
    const cacheDir = await createCacheDir()
    const fileIndex = join(cacheDir, 'desktop-file-index-v1.json')
    await writeFile(fileIndex, '{"schemaVersion":999}', 'utf8')

    await cleanupWorkspaceCache(tempRoot)

    expect(existsSync(fileIndex)).toBe(false)
  })

  it('keeps PascalCase welcome suggestions persisted by AppServer', async () => {
    const cacheDir = await createCacheDir()
    const suggestions = join(cacheDir, 'welcome-suggestions.json')
    await writeFile(suggestions, pascalCaseWelcomeSuggestionsCache, 'utf8')

    await cleanupWorkspaceCache(tempRoot)

    expect(existsSync(suggestions)).toBe(true)
  })

  it('keeps BOM-prefixed welcome suggestions persisted by AppServer', async () => {
    const cacheDir = await createCacheDir()
    const suggestions = join(cacheDir, 'welcome-suggestions.json')
    await writeFile(suggestions, `\uFEFF${pascalCaseWelcomeSuggestionsCache}`, 'utf8')

    await cleanupWorkspaceCache(tempRoot)

    expect(existsSync(suggestions)).toBe(true)
  })

  it('keeps malformed welcome suggestions for AppServer to validate', async () => {
    const cacheDir = await createCacheDir()
    const suggestions = join(cacheDir, 'welcome-suggestions.json')
    await writeFile(suggestions, '{not-json', 'utf8')

    await cleanupWorkspaceCache(tempRoot)

    expect(existsSync(suggestions)).toBe(true)
  })

  it('removes welcome suggestions temp files', async () => {
    const cacheDir = await createCacheDir()
    const suggestionsTemp = join(cacheDir, 'welcome-suggestions.json.tmp')
    await writeFile(suggestionsTemp, pascalCaseWelcomeSuggestionsCache, 'utf8')

    await cleanupWorkspaceCache(tempRoot)

    expect(existsSync(suggestionsTemp)).toBe(false)
  })
})
