import { app } from 'electron'
import { execFile } from 'child_process'
import { existsSync } from 'fs'
import { join } from 'path'
import net from 'net'

export interface ChromeOpenRequest {
  url?: string
}

export interface ChromeSetupStatus {
  extension: unknown
  nativeHost: unknown
  chromeRunning: unknown
  installedBrowsers: unknown
  bridge: unknown
}

function pluginSourceRootFromAppPath(): string {
  return join(app.getAppPath(), '..', 'src', 'DotCraft.Core', 'Plugins', 'BuiltIn', 'chrome')
}

export function resolveChromePluginRoot(workspacePath?: string): string {
  const workspace = workspacePath?.trim()
  if (workspace) {
    const installed = join(workspace, '.craft', 'plugins', 'chrome')
    if (existsSync(installed)) return installed

    const source = join(workspace, 'src', 'DotCraft.Core', 'Plugins', 'BuiltIn', 'chrome')
    if (existsSync(source)) return source

    return installed
  }

  return pluginSourceRootFromAppPath()
}

function chromeScriptPath(workspacePath: string | undefined, scriptName: string): string {
  return join(resolveChromePluginRoot(workspacePath), 'scripts', scriptName)
}

function resolveNodeRuntime(): { command: string; env: NodeJS.ProcessEnv } {
  if (app.isPackaged) {
    return {
      command: process.execPath,
      env: { ...process.env, ELECTRON_RUN_AS_NODE: '1' }
    }
  }
  return { command: 'node', env: { ...process.env } }
}

export function runChromeSetupScript(
  workspacePath: string | undefined,
  scriptName: string,
  args: string[]
): Promise<unknown> {
  return new Promise((resolve) => {
    const scriptPath = chromeScriptPath(workspacePath, scriptName)
    if (!existsSync(scriptPath)) {
      resolve({ ok: false, script: scriptPath, error: 'Script not found.' })
      return
    }

    const runtime = resolveNodeRuntime()
    execFile(runtime.command, [scriptPath, ...args], { timeout: 10_000, env: runtime.env }, (error, stdout, stderr) => {
      const text = String(stdout || '').trim()
      let parsed: unknown = text
      if (text) {
        try {
          parsed = JSON.parse(text)
        } catch {
          parsed = text
        }
      }
      if (error) {
        resolve({
          ok: false,
          script: scriptPath,
          error: error.message,
          stderr: String(stderr || '').trim(),
          result: parsed
        })
        return
      }
      resolve(parsed || { ok: true, script: scriptPath })
    })
  })
}

export async function checkChromeSetup(workspacePath?: string): Promise<ChromeSetupStatus> {
  const [extension, nativeHost, chromeRunning, installedBrowsers, bridge] = await Promise.all([
    runChromeSetupScript(workspacePath, 'check-extension-installed.js', ['--json']),
    runChromeSetupScript(workspacePath, 'check-native-host-manifest.js', ['--json']),
    runChromeSetupScript(workspacePath, 'chrome-is-running.js', ['--check', '--json']),
    runChromeSetupScript(workspacePath, 'installed-browsers.js', ['--check', '--json']),
    checkChromeBridge()
  ])
  return { extension, nativeHost, chromeRunning, installedBrowsers, bridge }
}

export function checkChromeBridge(): Promise<unknown> {
  const port = Number.parseInt(process.env.DOTCRAFT_CHROME_BRIDGE_PORT || '32177', 10)
  if (!Number.isFinite(port) || port <= 0) {
    return Promise.resolve({ ok: false, error: 'Invalid Chrome bridge port.' })
  }

  return new Promise((resolve) => {
    const socket = net.createConnection({ host: '127.0.0.1', port })
    let buffer = ''
    let settled = false
    let timer: ReturnType<typeof setTimeout>
    const request = JSON.stringify({ id: 1, method: 'user.openTabs', params: {} }) + '\n'
    const finish = (payload: unknown): void => {
      if (settled) return
      settled = true
      clearTimeout(timer)
      socket.destroy()
      resolve(payload)
    }
    timer = setTimeout(() => {
      finish({ ok: false, error: 'Chrome bridge did not respond.' })
    }, 1_000)

    socket.setEncoding('utf8')
    socket.on('connect', () => {
      socket.write(request)
    })
    socket.on('data', (chunk) => {
      buffer += chunk
      const newline = buffer.indexOf('\n')
      if (newline < 0) return
      const line = buffer.slice(0, newline).trim()
      if (!line) return
      try {
        const parsed = JSON.parse(line) as { ok?: boolean; error?: unknown }
        finish(parsed.ok === true ? { ok: true } : { ok: false, error: normalizeBridgeError(parsed.error) })
      } catch {
        finish({ ok: false, error: 'Chrome bridge returned invalid JSON.' })
      }
    })
    socket.on('error', (error) => {
      finish({ ok: false, error: normalizeBridgeError(error) })
    })
    socket.on('close', () => {
      finish({ ok: false, error: 'Chrome bridge closed before responding.' })
    })
  })
}

function normalizeBridgeError(error: unknown): string {
  if (error && typeof error === 'object' && 'message' in error && typeof error.message === 'string') {
    return error.message
  }
  return typeof error === 'string' ? error : 'Chrome bridge is not connected.'
}

export async function installChromeNativeHost(workspacePath?: string): Promise<unknown> {
  return runChromeSetupScript(workspacePath, 'installManifest.mjs', [])
}

export async function openChromeWindow(
  workspacePath: string | undefined,
  request: ChromeOpenRequest = {}
): Promise<unknown> {
  const url = normalizeChromeOpenUrl(request.url)
  return runChromeSetupScript(workspacePath, 'open-chrome-window.js', ['--json', url])
}

function normalizeChromeOpenUrl(value?: string): string {
  const trimmed = value?.trim()
  if (!trimmed) return 'about:blank'
  if (trimmed === 'about:blank') return trimmed
  if (/^https?:\/\//i.test(trimmed)) return trimmed
  if (/^chrome:\/\/extensions(?:\/|\?|$)/i.test(trimmed)) return trimmed
  return 'about:blank'
}
