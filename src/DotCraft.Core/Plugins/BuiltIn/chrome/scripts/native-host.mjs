#!/usr/bin/env node
import { spawn } from 'node:child_process';
import net from 'node:net';

const PORT = Number.parseInt(process.env.DOTCRAFT_CHROME_BRIDGE_PORT || '32177', 10);
const DESKTOP_DEEP_LINK_PORT = Number.parseInt(process.env.DOTCRAFT_DESKTOP_DEEPLINK_PORT || '32178', 10);
const HOST = '127.0.0.1';

let stdinBuffer = Buffer.alloc(0);
let nextExtensionRequestId = 1;
const extensionPending = new Map();
const tcpClients = new Set();
const DOTCRAFT_CHROME_SETTINGS_URL = 'dotcraft://settings/computer-control/chrome';

function sendNativeMessage(message) {
  const body = Buffer.from(JSON.stringify(message), 'utf8');
  const header = Buffer.alloc(4);
  header.writeUInt32LE(body.length, 0);
  process.stdout.write(Buffer.concat([header, body]));
}

function sendTcp(socket, message) {
  socket.write(JSON.stringify(message) + '\n', 'utf8');
}

function forwardToExtension(method, params) {
  const id = nextExtensionRequestId++;
  sendNativeMessage({ type: 'dotcraft-request', id, method, params });
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      extensionPending.delete(id);
      reject(new Error(`Chrome extension command '${method}' timed out.`));
    }, 30000);
    extensionPending.set(id, { resolve, reject, timer });
  });
}

function openDotCraftChromeSettingsViaProtocol() {
  const platform = process.platform;
  let command;
  let args;

  if (platform === 'win32') {
    command = 'cmd.exe';
    args = ['/c', 'start', '', DOTCRAFT_CHROME_SETTINGS_URL];
  } else if (platform === 'darwin') {
    command = 'open';
    args = [DOTCRAFT_CHROME_SETTINGS_URL];
  } else {
    command = 'xdg-open';
    args = [DOTCRAFT_CHROME_SETTINGS_URL];
  }

  const child = spawn(command, args, {
    detached: true,
    stdio: 'ignore',
    windowsHide: true
  });
  child.unref();
}

function requestDesktopChromeSettings() {
  if (!Number.isFinite(DESKTOP_DEEP_LINK_PORT) || DESKTOP_DEEP_LINK_PORT <= 0) {
    return Promise.reject(new Error('Invalid DotCraft Desktop deep link port.'));
  }

  return new Promise((resolve, reject) => {
    const socket = net.createConnection({ host: HOST, port: DESKTOP_DEEP_LINK_PORT });
    let buffer = '';
    let settled = false;
    let timer;
    const finish = (error) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      socket.destroy();
      if (error) reject(error);
      else resolve();
    };

    timer = setTimeout(() => {
      finish(new Error('DotCraft Desktop did not respond.'));
    }, 600);

    socket.setEncoding('utf8');
    socket.on('connect', () => {
      socket.write(JSON.stringify({ type: 'openChromeSettings' }) + '\n', 'utf8');
    });
    socket.on('data', (chunk) => {
      buffer += chunk;
      const newline = buffer.indexOf('\n');
      if (newline < 0) return;
      const line = buffer.slice(0, newline).trim();
      if (!line) return;
      try {
        const response = JSON.parse(line);
        finish(response?.ok === true ? null : new Error(response?.error || 'DotCraft Desktop rejected the request.'));
      } catch {
        finish(new Error('DotCraft Desktop returned invalid JSON.'));
      }
    });
    socket.on('error', (error) => finish(error));
    socket.on('close', () => finish(new Error('DotCraft Desktop closed the connection.')));
  });
}

async function openDotCraftChromeSettings() {
  try {
    await requestDesktopChromeSettings();
    return;
  } catch {
    openDotCraftChromeSettingsViaProtocol();
  }
}

function handleExtensionMessage(message) {
  if (message?.type === 'dotcraft-open-settings') {
    openDotCraftChromeSettings()
      .then(() => {
        sendNativeMessage({ type: 'dotcraft-settings-opened', ok: true });
      })
      .catch((error) => {
        sendNativeMessage({
          type: 'dotcraft-host-error',
          error: error instanceof Error ? error.message : String(error)
        });
      });
    return;
  }

  if (message?.type === 'dotcraft-response') {
    const pending = extensionPending.get(message.id);
    if (!pending) return;
    clearTimeout(pending.timer);
    extensionPending.delete(message.id);
    if (message.ok === false) pending.reject(new Error(message.error || 'Chrome extension command failed.'));
    else pending.resolve(message.result);
    return;
  }

  for (const socket of tcpClients) {
    sendTcp(socket, { event: message });
  }
}

function handleNativeData(chunk) {
  stdinBuffer = Buffer.concat([stdinBuffer, chunk]);
  while (stdinBuffer.length >= 4) {
    const length = stdinBuffer.readUInt32LE(0);
    if (stdinBuffer.length < length + 4) return;
    const body = stdinBuffer.subarray(4, 4 + length);
    stdinBuffer = stdinBuffer.subarray(4 + length);
    try {
      handleExtensionMessage(JSON.parse(body.toString('utf8')));
    } catch (error) {
      sendNativeMessage({
        type: 'dotcraft-host-error',
        error: error instanceof Error ? error.message : String(error)
      });
    }
  }
}

function handleTcpData(socket, state, chunk) {
  state.buffer += chunk;
  for (;;) {
    const newline = state.buffer.indexOf('\n');
    if (newline < 0) break;
    const line = state.buffer.slice(0, newline).trim();
    state.buffer = state.buffer.slice(newline + 1);
    if (!line) continue;
    let message;
    try {
      message = JSON.parse(line);
    } catch (error) {
      sendTcp(socket, { ok: false, error: 'Invalid JSON request.' });
      continue;
    }

    forwardToExtension(message.method, message.params ?? {})
      .then((result) => sendTcp(socket, { id: message.id, ok: true, result }))
      .catch((error) => sendTcp(socket, {
        id: message.id,
        ok: false,
        error: error instanceof Error ? error.message : String(error)
      }));
  }
}

const server = net.createServer((socket) => {
  const state = { buffer: '' };
  tcpClients.add(socket);
  socket.setEncoding('utf8');
  socket.on('data', (chunk) => handleTcpData(socket, state, chunk));
  socket.on('close', () => tcpClients.delete(socket));
  socket.on('error', () => tcpClients.delete(socket));
});

server.listen(PORT, HOST, () => {
  sendNativeMessage({ type: 'dotcraft-host-ready', port: PORT });
});
server.on('error', (error) => {
  sendNativeMessage({
    type: 'dotcraft-host-error',
    error: error instanceof Error ? error.message : String(error)
  });
});

process.stdin.on('data', handleNativeData);
process.stdin.on('end', () => process.exit(0));
process.stdin.resume();
