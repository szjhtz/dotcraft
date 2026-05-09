#!/usr/bin/env node
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const metadata = JSON.parse(fs.readFileSync(path.join(__dirname, 'extension-id.json'), 'utf8'));
const scriptPath = path.join(__dirname, 'native-host.mjs');
const nodePath = process.execPath;

function wrapperPath() {
  if (process.env.DOTCRAFT_CHROME_NATIVE_HOST_PATH) return process.env.DOTCRAFT_CHROME_NATIVE_HOST_PATH;
  if (process.platform === 'win32') {
    return path.join(os.homedir(), 'AppData', 'Local', 'DotCraft', 'chrome-extension', 'dotcraft-chrome-host.cmd');
  }
  return path.join(os.homedir(), '.local', 'share', 'dotcraft', 'chrome-extension', 'dotcraft-chrome-host.sh');
}

function writeWrapper(target) {
  fs.mkdirSync(path.dirname(target), { recursive: true });
  if (process.platform === 'win32') {
    fs.writeFileSync(target, `@echo off\r\n"${nodePath}" "${scriptPath}"\r\n`);
    return;
  }

  fs.writeFileSync(target, `#!/bin/sh\nexec "${nodePath}" "${scriptPath}"\n`);
  fs.chmodSync(target, 0o755);
}

const hostPath = wrapperPath();
writeWrapper(hostPath);
const manifest = {
  name: metadata.extensionHostName,
  description: 'DotCraft Chrome native messaging host',
  type: 'stdio',
  path: hostPath,
  allowed_origins: [`chrome-extension://${metadata.extensionId}/`]
};

function manifestPath() {
  if (process.env.DOTCRAFT_CHROME_NATIVE_HOST_MANIFEST_PATH) {
    return process.env.DOTCRAFT_CHROME_NATIVE_HOST_MANIFEST_PATH;
  }
  if (process.platform === 'win32') {
    return path.join(os.homedir(), 'AppData', 'Local', 'DotCraft', 'chrome-extension', `${metadata.extensionHostName}.json`);
  }
  if (process.platform === 'darwin') {
    return path.join(os.homedir(), 'Library', 'Application Support', 'Google', 'Chrome', 'NativeMessagingHosts', `${metadata.extensionHostName}.json`);
  }
  return path.join(os.homedir(), '.config', 'google-chrome', 'NativeMessagingHosts', `${metadata.extensionHostName}.json`);
}

const target = manifestPath();
fs.mkdirSync(path.dirname(target), { recursive: true });
fs.writeFileSync(target, `${JSON.stringify(manifest, null, 2)}\n`);

if (process.platform === 'win32') {
  execFileSync('reg', [
    'add',
    `HKCU\\Software\\Google\\Chrome\\NativeMessagingHosts\\${metadata.extensionHostName}`,
    '/ve',
    '/t',
    'REG_SZ',
    '/d',
    target,
    '/f'
  ], { stdio: 'ignore' });
}

  process.stdout.write(JSON.stringify({
  ok: true,
  manifestPath: target,
  hostPath,
  extensionId: metadata.extensionId,
  hostExists: fs.existsSync(hostPath)
}, null, 2) + '\n');
