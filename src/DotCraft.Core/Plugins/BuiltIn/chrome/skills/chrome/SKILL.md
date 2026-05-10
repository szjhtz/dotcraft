---
name: chrome
description: "Use the user's existing Chrome browser through the DotCraft Chrome extension backend. Use for authenticated/profile-dependent pages, existing Chrome tabs, cookies-backed sessions, installed extensions, and workflows where the Desktop embedded browser is not enough."
tools: NodeReplJs
---

# Chrome Browser

Use `NodeReplJs` for Chrome work. The runtime is thread-bound and persistent; `globalThis` state survives between calls.

Chrome is the user's real browser profile. Treat browser contents as sensitive.

## First Cell

Start with this guarded cell. It avoids redeclaring `const tab` across REPL calls and works when the cell is rerun:

```js
let chromeBackendReady = false;
try {
  chromeBackendReady = (await agent.browsers.list()).some((item) => item?.id === "extension");
} catch {
  chromeBackendReady = false;
}
if (!chromeBackendReady) {
  const { setupAtlasRuntime } = await import(dotcraft.chromeBrowserClientPath);
  await setupAtlasRuntime({ globals: globalThis, backend: "extension" });
}
if (!globalThis.browser) {
  globalThis.browser = await agent.browsers.get("extension");
}
await browser.nameSession("Chrome");
if (typeof tab === "undefined") {
  try {
    globalThis.tab = await browser.tabs.selected();
  } catch {
    globalThis.tab = await browser.tabs.new();
  }
}
```

After setup, run a lightweight check such as `await browser.user.openTabs()`. If it fails, wait about two seconds and retry once.

If retry still fails with a bridge connection error, use `await dotcraft.chrome.checkSetup()` and follow this recovery order:

- Chrome not installed: tell the user DotCraft Chrome needs Google Chrome.
- Chrome not running: ask before launching Chrome or opening a Chrome window.
- Extension missing or disabled: ask the user to install/enable the DotCraft Chrome extension.
- Native host manifest missing or invalid: ask the user to reinstall or repair the DotCraft Chrome plugin setup.
- Checks pass but bridge is disconnected: ask the user to click the DotCraft Chrome extension icon once, then retry.

Do not quote raw `ECONNREFUSED` output as the final answer.

## Observe After Actions

Navigation and clicks can take a moment to become visible to the bridge. After opening a page or changing app state, observe the current tab before deciding the action failed:

```js
await tab.goto("https://example.com");
globalThis.lastObservation = await tab.observe();
console.log(lastObservation.url, lastObservation.title);
```

For clicks or submits that should navigate, wrap the action:

```js
await tab.playwright.expectNavigation(
  () => tab.playwright.getByText("Open").click(),
  { timeoutMs: 10000 }
);
globalThis.lastObservation = await tab.observe();
```

If the URL or title looks stale, wait or observe once before creating a new tab. Do not open a replacement tab just because the immediate return value looked unchanged.

## REPL Sandbox Rules

DotCraft's Node REPL is sandboxed. Do not use `require`, `process`, `process.cwd()`, `__dirname`, or `dotcraft.workspace`.

Use these safe globals instead:

- `dotcraft.workspacePath`
- `dotcraft.chromePluginRoot`
- `dotcraft.chromeScriptsPath`
- `dotcraft.chrome.checkSetup()`
- `dotcraft.chrome.checkExtension()`
- `dotcraft.chrome.checkNativeHost()`

For setup diagnostics, prefer:

```js
console.log(JSON.stringify(await dotcraft.chrome.checkSetup(), null, 2));
```

If shell/Exec is available outside the REPL, setup scripts may also be run from `dotcraft.chromeScriptsPath`. Keep diagnostics read-only.

## Supported API

Use these Chrome APIs. Do not invent alternatives unless you have checked `describeApi()` first.

- `agent.browsers.list()`
- `browser.describeApi()`
- `browser.nameSession(name)`
- `browser.user.openTabs(options)`
- `browser.user.claimTab(tab, options)`
- `browser.tabs.list(options)`
- `browser.tabs.selected(options)`
- `browser.tabs.get(tab)`
- `browser.tabs.new(options)`
- `browser.tabs.content({ urls, contentType, maxLength, timeoutMs })`
- `browser.tabs.read(options)`
- `browser.tabs.finalize({ keep })`
- `tab.goto(url, options)`
- `tab.reload(options)`
- `tab.back(options)`
- `tab.forward(options)`
- `tab.close()`
- `tab.title()`
- `tab.url()`
- `tab.content.text(options)`
- `tab.content.html(options)`
- `tab.content.read(options)`
- `tab.content.get(options)`
- `tab.domSnapshot(options)`
- `tab.observe(options)`
- `tab.evaluate(fn, arg)`
- `tab.screenshot(options)`

The `tab.playwright` compatibility subset supports:

- `locator(selector).count()`
- `locator(selector).click()`
- `locator(selector).dblclick()`
- `locator(selector).fill(value)`
- `locator(selector).type(value)`
- `locator(selector).press(key)`
- `locator(selector).textContent()`
- `locator(selector).allTextContents()`
- `locator(selector).innerText()`
- `locator(selector).getAttribute(name)`
- `locator(selector).isVisible()`
- `locator(selector).isEnabled()`
- `locator(selector).waitFor({ state, timeoutMs })`
- `locator(selector).check()`
- `locator(selector).uncheck()`
- `locator(selector).setChecked(checked)`
- `locator(selector).selectOption(value)`
- `getByText`, `getByRole`, `getByLabel`, `getByPlaceholder`, `getByTestId`
- `domSnapshot(options)`, `observe(options)`, `screenshot(options)`, `waitForLoadState({ state, timeoutMs })`, `waitForTimeout(ms)`, `waitForURL(url, options)`, `expectNavigation(action, options)`
- `waitForEvent("filechooser", { timeoutMs })`

File chooser objects support:

- `chooser.isMultiple()`
- `chooser.setFiles(["C:\\absolute\\path\\file.txt"])`

The `tab.cua` compatibility subset supports:

- `get_visible_screenshot(options)`
- `click({ x, y })`
- `double_click({ x, y })`
- `scroll({ x, y, deltaX, deltaY })`
- `type(text)`
- `keypress(keyOrKeys)`
- `move({ x, y })`

The `tab.dom_cua` compatibility subset supports:

- `get_visible_dom(options)`
- `click({ node_id })`
- `double_click({ node_id })`
- `scroll({ node_id, deltaX, deltaY })`
- `type({ node_id, text })`
- `keypress({ node_id, key })`

`networkidle` is treated as `document.readyState === "complete"` in this backend. DotCraft locator methods are a page-level compatibility subset; nested locators are not a full Playwright scoping model. For complex DOMs, inspect with `domSnapshot()` and then use a stable page-level locator.

Call content methods as functions. For example, use `await tab.content.text({ maxLength: 30000 })`, not `tab.content.text`.

## File Uploads

Use the Playwright-style file chooser flow. Start waiting before clicking the file input or its label:

```js
const chooserPromise = tab.playwright.waitForEvent("filechooser", { timeoutMs: 10000 });
await tab.playwright.locator('input[type="file"]').click();
const chooser = await chooserPromise;
await chooser.setFiles(["C:\\absolute\\path\\file.txt"]);
```

Use absolute local paths only. For multiple files, check `await chooser.isMultiple()` first; if the input is not multiple, ask the user which single file to upload or use a more specific `input[type="file"]` locator.

If the page uses a custom upload button and the chooser cannot be located, try the underlying `input[type="file"]` or a stable label/role locator. If that still fails, ask the user to upload manually.

## Unsupported APIs

These objects/methods exist only to fail clearly or report no optional capabilities:

- `browser.capabilities.list()` and `tab.capabilities.list()` return `[]`.
- `tab.playwright.frameLocator(...)`, `tab.clipboard`, downloads, history, media downloads, complex frames, and `tab.cua.drag(...)` are not implemented.
- `tab.playwright.waitForEvent("download")` is unsupported. Only `"filechooser"` is implemented.

Do not work around unsupported Chrome APIs with profile file inspection, cookies, local storage, history databases, or unrelated automation mechanisms.

## Setup Checks

Available read-only scripts:

- `check-extension-installed.js --json`
- `check-native-host-manifest.js --json`
- `chrome-is-running.js --check --json`
- `installed-browsers.js --check --json`
- `open-chrome-window.js --dry-run --json`

Use `open-chrome-window.js` without `--dry-run` only after the user agrees. Do not install, repair, or rewrite Chrome state without user consent.

## User Tabs

- Use `browser.user.openTabs()` to inspect visible user tabs by title, URL, recency, and group.
- Claim an existing tab only by passing the exact returned tab object to `browser.user.claimTab(tab)`.
- Do not guess tab IDs.
- Prefer reusing an already selected or claimed tab over creating new tabs.
- Before finishing, call `browser.tabs.finalize({ keep: [tab] })` exactly once as the final Chrome browser action of the turn. Use `keep: []` when no agent-created tabs should remain. Do not use `keep: true`.
- Keep only tabs that are deliverables or explicit handoffs.

## Playwright Discipline

Use stable locators first: role, label, placeholder, test id, then CSS. Keep actions small and verify page state with `observe()`, `domSnapshot()`, or targeted content reads. If an action fails because the bridge is disconnected, retry once, then use the recovery flow above.

## Safety

- Do not inspect cookies, passwords, Chrome profile databases, local storage, session stores, or browser history files.
- Ask before navigating to a new external site, claiming a user tab, uploading a file, downloading a file, or touching sensitive pages.
- Do not read or write Chrome profile files except the documented setup checks.
