import { rgPath as rawRgPath } from '@vscode/ripgrep'

export const DOTCRAFT_RG_PATH_ENV = 'DOTCRAFT_RG_PATH'

export interface DotCraftRuntimeTools {
  ripgrepPath?: string
}

export function resolveBundledRipgrepPath(): string {
  return rawRgPath.replace(/\bapp\.asar\b/g, 'app.asar.unpacked')
}

export function resolveDotCraftRuntimeTools(): DotCraftRuntimeTools {
  return { ripgrepPath: resolveBundledRipgrepPath() }
}

export function buildDotCraftRuntimeEnv(): NodeJS.ProcessEnv {
  return { [DOTCRAFT_RG_PATH_ENV]: resolveBundledRipgrepPath() }
}
