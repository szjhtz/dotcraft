import { rgPath as rawRgPath } from '@vscode/ripgrep'
import { app } from 'electron'
import * as path from 'path'

export const DOTCRAFT_RG_PATH_ENV = 'DOTCRAFT_RG_PATH'

export interface DotCraftRuntimeTools {
  ripgrepPath?: string
  nodeBin?: string
  nodeRunAsNode?: boolean
  modulesDir?: string
}

export function resolveBundledRipgrepPath(): string {
  return rawRgPath.replace(/\bapp\.asar\b/g, 'app.asar.unpacked')
}

export function resolveDotCraftRuntimeTools(): DotCraftRuntimeTools {
  return {
    ripgrepPath: resolveBundledRipgrepPath(),
    nodeBin: resolveBundledNodePath(),
    nodeRunAsNode: true,
    modulesDir: resolveBundledModulesDir()
  }
}

export function buildDotCraftRuntimeEnv(): NodeJS.ProcessEnv {
  return { [DOTCRAFT_RG_PATH_ENV]: resolveBundledRipgrepPath() }
}

export function resolveBundledNodePath(): string {
  return process.execPath
}

export function resolveBundledModulesDir(): string {
  if (app.isPackaged) {
    return path.join(process.resourcesPath, 'modules')
  }
  return path.resolve(__dirname, '../../../sdk/typescript/packages')
}
