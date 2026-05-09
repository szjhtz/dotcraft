#!/usr/bin/env node
const fs = require('fs');
const path = require('path');
const os = require('os');
const { execFileSync } = require('child_process');

const asJson = process.argv.includes('--json');
const metadata = JSON.parse(fs.readFileSync(path.join(__dirname, 'extension-id.json'), 'utf8'));
const expectedOrigin = `chrome-extension://${metadata.extensionId}/`;

function defaultManifestPath() {
  if (process.env.DOTCRAFT_CHROME_NATIVE_HOST_MANIFEST_PATH) {
    return process.env.DOTCRAFT_CHROME_NATIVE_HOST_MANIFEST_PATH;
  }
  if (process.platform === 'win32') {
    try {
      const output = execFileSync('reg', [
        'query',
        `HKCU\\Software\\Google\\Chrome\\NativeMessagingHosts\\${metadata.extensionHostName}`,
        '/ve'
      ], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] });
      const match = output.match(/REG_SZ\s+(.+)$/m);
      if (match) return match[1].trim();
    } catch {
      return path.join(os.homedir(), 'AppData', 'Local', 'DotCraft', 'chrome-extension', `${metadata.extensionHostName}.json`);
    }
  }
  if (process.platform === 'darwin') {
    return path.join(os.homedir(), 'Library', 'Application Support', 'Google', 'Chrome', 'NativeMessagingHosts', `${metadata.extensionHostName}.json`);
  }
  return path.join(os.homedir(), '.config', 'google-chrome', 'NativeMessagingHosts', `${metadata.extensionHostName}.json`);
}

function readManifest(file) {
  try {
    return JSON.parse(fs.readFileSync(file, 'utf8'));
  } catch {
    return null;
  }
}

const manifestPath = defaultManifestPath();
const manifest = readManifest(manifestPath);
const origins = Array.isArray(manifest?.allowed_origins) ? manifest.allowed_origins : [];
const result = {
  ok: manifest?.name === metadata.extensionHostName && origins.includes(expectedOrigin),
  hostName: metadata.extensionHostName,
  extensionId: metadata.extensionId,
  expectedOrigin,
  manifestPath,
  exists: fs.existsSync(manifestPath),
  nameMatches: manifest?.name === metadata.extensionHostName,
  allowedOriginMatches: origins.includes(expectedOrigin),
  hostPath: manifest?.path ?? null
};

if (asJson) {
  process.stdout.write(JSON.stringify(result, null, 2) + '\n');
} else if (result.ok) {
  console.log(`DotCraft Chrome Native Messaging host manifest is valid: ${manifestPath}`);
} else {
  console.log(`DotCraft Chrome Native Messaging host manifest is missing or invalid: ${manifestPath}`);
}

process.exit(result.ok ? 0 : 1);
