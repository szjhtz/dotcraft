#!/usr/bin/env node
import net from 'node:net';

const PORT = Number.parseInt(process.env.DOTCRAFT_CHROME_BRIDGE_PORT || '32177', 10);
const HOST = '127.0.0.1';

let stdinBuffer = Buffer.alloc(0);
let nextExtensionRequestId = 1;
const extensionPending = new Map();
const tcpClients = new Set();

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

function handleExtensionMessage(message) {
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
