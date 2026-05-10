import net from "node:net";

const DEFAULT_HOST = "127.0.0.1";
const DEFAULT_PORT = Number(process.env.DOTCRAFT_CHROME_BRIDGE_PORT || 32177);
const DEFAULT_TIMEOUT_MS = 15000;
const BRIDGE_UNAVAILABLE_MESSAGE =
  "Chrome extension bridge is not connected; click the DotCraft Chrome extension after installing the native host manifest, then retry.";

function normalizeBridgeError(error) {
  const code = error?.code;
  if (code === "ECONNREFUSED" || code === "ECONNRESET" || code === "EPIPE" || code === "ETIMEDOUT") {
    const wrapped = new Error(BRIDGE_UNAVAILABLE_MESSAGE);
    wrapped.cause = error;
    wrapped.code = code;
    return wrapped;
  }
  return error;
}

function tabReference(tab) {
  if (!tab || typeof tab !== "object") {
    return tab;
  }
  if (tab.info && typeof tab.info === "object") {
    return tabReference(tab.info);
  }
  return {
    id: tab.id,
    tabId: tab.tabId ?? tab.id,
    windowId: tab.windowId,
    title: tab.title,
    url: tab.url,
    active: tab.active,
    index: tab.index,
    claimed: tab.claimed === true,
    loading: tab.loading === true,
  };
}

function normalizeFinalizeOptions(options = {}) {
  const keep = options.keep ?? [];
  if (!Array.isArray(keep)) {
    throw new Error("browser.tabs.finalize({ keep }) requires keep must be an array of tabs or tab ids.");
  }
  return {
    ...options,
    keep: keep.map((item) => {
      if (typeof item === "number" || typeof item === "string") {
        return item;
      }
      return tabReference(item);
    }),
  };
}

function truncateContent(value, maxLength) {
  if (typeof value !== "string" || typeof maxLength !== "number" || maxLength < 0) {
    return value;
  }
  return value.length > maxLength ? value.slice(0, maxLength) : value;
}

function unsupportedApi(name) {
  throw new Error(`DotCraft Chrome does not support ${name} yet. Use the documented Chrome compatibility subset or ask the user before choosing another browser-control path.`);
}

function normalizeWaitForLoadStateArgs(stateOrOptions = "load", options = {}) {
  if (stateOrOptions && typeof stateOrOptions === "object") {
    return {
      state: stateOrOptions.state || "load",
      options: stateOrOptions,
    };
  }
  return {
    state: stateOrOptions || "load",
    options,
  };
}

class EmptyCapabilityCollection {
  async list() {
    return [];
  }

  async get(id) {
    unsupportedApi(`capability "${id}"`);
  }
}

class ChromeFileChooser {
  constructor(tab, info = {}) {
    this.tab = tab;
    this.info = info;
  }

  async isMultiple() {
    return await this.tab.client.request("tab.fileChooserIsMultiple", {
      tab: tabReference(this.tab.info),
      fileChooser: this.info,
    });
  }

  async setFiles(files) {
    const paths = Array.isArray(files) ? files : [files];
    return await this.tab.client.request("tab.fileChooserSetFiles", {
      tab: tabReference(this.tab.info),
      fileChooser: this.info,
      files: paths.map((file) => String(file)),
    });
  }

  describeApi() {
    return ["isMultiple()", "setFiles(files)"];
  }
}

class ChromeCuaApi {
  constructor(tab) {
    this.tab = tab;
  }

  async get_visible_screenshot(options = {}) {
    return await this.tab.screenshot(options);
  }

  async click(options = {}) {
    return await this.action("click", options);
  }

  async double_click(options = {}) {
    return await this.action("double_click", options);
  }

  async scroll(options = {}) {
    return await this.action("scroll", options);
  }

  async type(textOrOptions = {}) {
    const options = typeof textOrOptions === "string" ? { text: textOrOptions } : textOrOptions;
    return await this.action("type", options);
  }

  async keypress(keyOrOptions = {}) {
    const options = typeof keyOrOptions === "string" || Array.isArray(keyOrOptions)
      ? { keys: Array.isArray(keyOrOptions) ? keyOrOptions : [keyOrOptions] }
      : keyOrOptions;
    return await this.action("keypress", options);
  }

  async move(options = {}) {
    return await this.action("move", options);
  }

  async drag() {
    unsupportedApi("tab.cua.drag()");
  }

  async action(action, options) {
    return await this.tab.client.request("cua.action", {
      tab: tabReference(this.tab.info),
      action,
      options,
    });
  }

  describeApi() {
    return ["get_visible_screenshot()", "click(options)", "double_click(options)", "scroll(options)", "type(options)", "keypress(options)", "move(options)", "drag() unsupported"];
  }
}

class ChromeDomCuaApi {
  constructor(tab) {
    this.tab = tab;
  }

  async get_visible_dom(options = {}) {
    return await this.tab.client.request("domCua.visibleDom", {
      tab: tabReference(this.tab.info),
      options,
    });
  }

  async click(options = {}) {
    return await this.action("click", options);
  }

  async double_click(options = {}) {
    return await this.action("double_click", options);
  }

  async scroll(options = {}) {
    return await this.action("scroll", options);
  }

  async type(options = {}) {
    return await this.action("type", options);
  }

  async keypress(options = {}) {
    return await this.action("keypress", options);
  }

  async action(action, options) {
    return await this.tab.client.request("domCua.action", {
      tab: tabReference(this.tab.info),
      action,
      options,
    });
  }

  describeApi() {
    return ["get_visible_dom()", "click(options)", "double_click(options)", "scroll(options)", "type(options)", "keypress(options)"];
  }
}

class ChromeBridgeClient {
  constructor(options = {}) {
    this.host = options.host || DEFAULT_HOST;
    this.port = options.port || DEFAULT_PORT;
    this.timeoutMs = options.timeoutMs || DEFAULT_TIMEOUT_MS;
    this.socket = null;
    this.buffer = "";
    this.nextId = 1;
    this.pending = new Map();
  }

  async ensureConnected() {
    if (this.socket && !this.socket.destroyed) {
      return;
    }

    await new Promise((resolve, reject) => {
      const socket = net.createConnection({ host: this.host, port: this.port });
      let settled = false;

      const timer = setTimeout(() => {
        socket.destroy();
        if (!settled) {
          settled = true;
          reject(normalizeBridgeError(Object.assign(new Error("Chrome bridge connection timed out."), { code: "ETIMEDOUT" })));
        }
      }, this.timeoutMs);

      socket.setEncoding("utf8");

      socket.on("connect", () => {
        clearTimeout(timer);
        settled = true;
        this.socket = socket;
        socket.on("data", (chunk) => this.handleData(chunk));
        socket.on("close", () => this.handleClose());
        socket.on("error", (error) => this.handleClose(normalizeBridgeError(error)));
        resolve();
      });

      socket.on("error", (error) => {
        clearTimeout(timer);
        if (!settled) {
          settled = true;
          reject(normalizeBridgeError(error));
          return;
        }
        this.handleClose(normalizeBridgeError(error));
      });
    });
  }

  handleData(chunk) {
    this.buffer += chunk;
    let newline = this.buffer.indexOf("\n");
    while (newline >= 0) {
      const line = this.buffer.slice(0, newline).trim();
      this.buffer = this.buffer.slice(newline + 1);
      if (line.length > 0) {
        this.handleMessage(line);
      }
      newline = this.buffer.indexOf("\n");
    }
  }

  handleMessage(line) {
    let message;
    try {
      message = JSON.parse(line);
    } catch (error) {
      console.warn("[DotCraft Chrome] Ignoring invalid bridge message", error);
      return;
    }
    const pending = this.pending.get(message.id);
    if (!pending) {
      return;
    }
    this.pending.delete(message.id);
    clearTimeout(pending.timer);
    if (message.error) {
      pending.reject(new Error(message.error.message || String(message.error)));
    } else {
      pending.resolve(message.result);
    }
  }

  handleClose(error) {
    const socket = this.socket;
    this.socket = null;
    if (socket) {
      socket.removeAllListeners();
    }
    const closeError = error || new Error("Chrome bridge connection closed.");
    for (const pending of this.pending.values()) {
      clearTimeout(pending.timer);
      pending.reject(closeError);
    }
    this.pending.clear();
  }

  async request(method, params = {}) {
    await this.ensureConnected();
    const id = this.nextId++;
    const payload = JSON.stringify({ id, method, params }) + "\n";

    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`Chrome bridge request timed out: ${method}`));
      }, this.timeoutMs);

      this.pending.set(id, { resolve, reject, timer });
      this.socket.write(payload, (error) => {
        if (error) {
          clearTimeout(timer);
          this.pending.delete(id);
          reject(normalizeBridgeError(error));
        }
      });
    });
  }

  close() {
    if (this.socket) {
      this.socket.end();
      this.socket.destroy();
    }
    this.handleClose();
  }
}

class ChromeLocator {
  constructor(tab, selector, options = {}) {
    this.tab = tab;
    this.selector = selector;
    this.options = options;
  }

  first() {
    return new ChromeLocator(this.tab, this.selector, { ...this.options, nth: 0 });
  }

  last() {
    return new ChromeLocator(this.tab, this.selector, { ...this.options, nth: -1 });
  }

  nth(index) {
    return new ChromeLocator(this.tab, this.selector, { ...this.options, nth: index });
  }

  locator(selector, options = {}) {
    return new ChromeLocator(this.tab, selector, options);
  }

  getByText(text, options = {}) {
    return this.tab.getByText(text, options);
  }

  getByRole(role, options = {}) {
    return this.tab.getByRole(role, options);
  }

  getByLabel(text, options = {}) {
    return this.tab.getByLabel(text, options);
  }

  getByPlaceholder(text, options = {}) {
    return this.tab.getByPlaceholder(text, options);
  }

  getByTestId(testId, options = {}) {
    return this.tab.getByTestId(testId, options);
  }

  async action(action, value, options = {}) {
    return await this.tab.client.request("locator.action", {
      tab: tabReference(this.tab.info),
      selector: this.selector,
      selectorOptions: this.options,
      action,
      value,
      options,
    });
  }

  async count(options = {}) {
    return await this.action("count", undefined, options);
  }

  async click(options = {}) {
    return await this.action("click", undefined, options);
  }

  async dblclick(options = {}) {
    return await this.action("dblclick", undefined, options);
  }

  async fill(value, options = {}) {
    return await this.action("fill", value, options);
  }

  async type(value, options = {}) {
    return await this.action("type", value, options);
  }

  async press(key, options = {}) {
    return await this.action("press", key, options);
  }

  async textContent(options = {}) {
    return await this.action("textContent", undefined, options);
  }

  async innerText(options = {}) {
    return await this.action("innerText", undefined, options);
  }

  async getAttribute(name, options = {}) {
    return await this.action("getAttribute", name, options);
  }

  async isVisible(options = {}) {
    return await this.action("isVisible", undefined, options);
  }

  async isEnabled(options = {}) {
    return await this.action("isEnabled", undefined, options);
  }

  async waitFor(options = {}) {
    return await this.action("waitFor", undefined, options);
  }

  async check(options = {}) {
    return await this.action("check", true, options);
  }

  async uncheck(options = {}) {
    return await this.action("check", false, options);
  }

  async setChecked(checked, options = {}) {
    return await this.action("check", Boolean(checked), options);
  }

  async selectOption(value, options = {}) {
    return await this.action("selectOption", value, options);
  }

  async allTextContents(options = {}) {
    return await this.action("allTextContents", undefined, options);
  }

  all() {
    unsupportedApi("locator.all()");
  }

  filter() {
    unsupportedApi("locator.filter()");
  }

  and() {
    unsupportedApi("locator.and()");
  }

  or() {
    unsupportedApi("locator.or()");
  }

  describeApi() {
    return [
      "count()",
      "click()",
      "dblclick()",
      "fill(value)",
      "type(value)",
      "press(key)",
      "textContent()",
      "innerText()",
      "getAttribute(name)",
      "isVisible()",
      "isEnabled()",
      "waitFor(options)",
      "check(options)",
      "uncheck(options)",
      "setChecked(checked, options)",
      "selectOption(value, options)",
      "allTextContents(options)",
      "first()",
      "last()",
      "nth(index)",
    ];
  }
}

class ChromeContentApi {
  constructor(tab) {
    this.tab = tab;
  }

  async text(options = {}) {
    const value = await this.tab.client.request("tab.contentText", { tab: tabReference(this.tab.info), ...options });
    return truncateContent(value, options.maxLength);
  }

  async html(options = {}) {
    const value = await this.tab.client.request("tab.contentHtml", { tab: tabReference(this.tab.info), ...options });
    return truncateContent(value, options.maxLength);
  }

  async read(options = {}) {
    const contentType = options.contentType || options.type || "text";
    return contentType === "html" ? await this.html(options) : await this.text(options);
  }

  async get(options = {}) {
    return await this.read(options);
  }

  describeApi() {
    return ["text(options)", "html(options)", "read(options)", "get(options)"];
  }
}

class ChromePlaywrightApi {
  constructor(tab) {
    this.tab = tab;
  }

  locator(selector, options = {}) {
    return this.tab.locator(selector, options);
  }

  getByText(text, options = {}) {
    return this.tab.getByText(text, options);
  }

  getByRole(role, options = {}) {
    return this.tab.getByRole(role, options);
  }

  getByLabel(text, options = {}) {
    return this.tab.getByLabel(text, options);
  }

  getByPlaceholder(text, options = {}) {
    return this.tab.getByPlaceholder(text, options);
  }

  getByTestId(testId, options = {}) {
    return this.tab.getByTestId(testId, options);
  }

  async screenshot(options = {}) {
    return await this.tab.screenshot(options);
  }

  async domSnapshot(options = {}) {
    return await this.tab.domSnapshot(options);
  }

  async observe(options = {}) {
    return await this.tab.observe(options);
  }

  async innerText(selector, options = {}) {
    return await this.locator(selector, options).innerText();
  }

  async getAttribute(selector, name, options = {}) {
    return await this.locator(selector, options).getAttribute(name);
  }

  async isVisible(selector, options = {}) {
    return await this.locator(selector, options).isVisible();
  }

  async press(selector, key, options = {}) {
    return await this.locator(selector, options).press(key);
  }

  async waitFor(selector, options = {}) {
    return await this.locator(selector, options).waitFor(options);
  }

  async waitForLoadState(stateOrOptions = "load", options = {}) {
    const normalized = normalizeWaitForLoadStateArgs(stateOrOptions, options);
    return await this.tab.client.request("tab.waitForLoadState", {
      tab: tabReference(this.tab.info),
      state: normalized.state,
      options: normalized.options,
    });
  }

  async waitForTimeout(ms) {
    await new Promise((resolve) => setTimeout(resolve, ms));
  }

  async waitForURL(url, options = {}) {
    return await this.tab.client.request("tab.waitForURL", {
      tab: tabReference(this.tab.info),
      url,
      options,
    });
  }

  async expectNavigation(action, options = {}) {
    if (typeof action !== "function") {
      throw new Error("tab.playwright.expectNavigation(action, options) requires an async action function.");
    }
    const previousUrl = await this.tab.url();
    const result = await action();
    if (options.url) {
      await this.waitForURL(options.url, options);
    } else {
      await this.tab.client.request("tab.waitForNavigation", {
        tab: tabReference(this.tab.info),
        previousUrl,
        options,
      });
    }
    if (options.waitUntil === "load") {
      await this.waitForLoadState("load", options);
    }
    return result;
  }

  async waitForEvent(event, options = {}) {
    if (event !== "filechooser") {
      unsupportedApi(`playwright.waitForEvent("${event}")`);
    }
    const info = await this.tab.client.request("tab.waitForFileChooser", {
      tab: tabReference(this.tab.info),
      options,
    });
    return new ChromeFileChooser(this.tab, info);
  }

  frameLocator() {
    unsupportedApi("playwright.frameLocator()");
  }

  describeApi() {
    return [
      "locator(selector)",
      "getByText(text)",
      "getByRole(role)",
      "getByLabel(text)",
      "getByPlaceholder(text)",
      "getByTestId(testId)",
      "screenshot(options)",
      "domSnapshot(options)",
      "observe(options)",
      "waitForLoadState({ state, timeoutMs })",
      "waitForTimeout(ms)",
      "waitForURL(url)",
      "expectNavigation(action, options)",
      "waitForEvent(\"filechooser\", options)",
      "frameLocator(selector) unsupported",
      "innerText(selector)",
      "getAttribute(selector, name)",
      "isVisible(selector)",
      "press(selector, key)",
      "waitFor(selector)",
    ];
  }
}

class ChromeTab {
  constructor(browser, tab) {
    this.browser = browser;
    this.client = browser.client;
    this.info = tabReference(tab);
    this.content = new ChromeContentApi(this);
    this.playwright = new ChromePlaywrightApi(this);
    this.capabilities = new EmptyCapabilityCollection();
    this.clipboard = {
      read: async () => unsupportedApi("tab.clipboard.read()"),
      readText: async () => unsupportedApi("tab.clipboard.readText()"),
      write: async () => unsupportedApi("tab.clipboard.write()"),
      writeText: async () => unsupportedApi("tab.clipboard.writeText()"),
    };
    this.cua = new ChromeCuaApi(this);
    this.dom_cua = new ChromeDomCuaApi(this);
    this.dev = {
      logs: async () => [],
    };
  }

  async goto(url, options = {}) {
    const result = await this.client.request("tab.goto", { tab: tabReference(this.info), url, options });
    this.info = tabReference(result || this.info);
    return this;
  }

  async reload(options = {}) {
    return await this.client.request("tab.reload", { tab: tabReference(this.info), options });
  }

  async back(options = {}) {
    return await this.client.request("tab.back", { tab: tabReference(this.info), options });
  }

  async forward(options = {}) {
    return await this.client.request("tab.forward", { tab: tabReference(this.info), options });
  }

  async close() {
    return await this.client.request("tab.close", { tab: tabReference(this.info) });
  }

  async title() {
    return await this.client.request("tab.title", { tab: tabReference(this.info) });
  }

  async url() {
    return await this.client.request("tab.url", { tab: tabReference(this.info) });
  }

  async screenshot(options = {}) {
    return await this.client.request("tab.screenshot", { tab: tabReference(this.info), options });
  }

  async evaluate(pageFunction, arg) {
    const source = typeof pageFunction === "function" ? `return (${pageFunction.toString()})(arguments[0]);` : String(pageFunction);
    return await this.client.request("tab.evaluate", { tab: tabReference(this.info), source, arg });
  }

  async domSnapshot(options = {}) {
    return await this.client.request("tab.domSnapshot", { tab: tabReference(this.info), options });
  }

  async observe(options = {}) {
    const current = await this.client.request("tabs.get", { tab: tabReference(this.info) });
    this.info = tabReference(current || this.info);
    const observation = {
      tab: tabReference(this.info),
      url: this.info.url || "",
      title: this.info.title || "",
      loading: this.info.loading === true,
    };
    if (options.domSnapshot !== false) {
      try {
        observation.domSnapshot = await this.domSnapshot(options.domSnapshotOptions ?? {});
      } catch (error) {
        observation.domError = error instanceof Error ? error.message : String(error);
      }
    }
    if (options.screenshot === true) {
      try {
        observation.screenshot = await this.screenshot(options.screenshotOptions ?? {});
      } catch (error) {
        observation.screenshotError = error instanceof Error ? error.message : String(error);
      }
    }
    return observation;
  }

  locator(selector, options = {}) {
    return new ChromeLocator(this, selector, options);
  }

  getByText(text, options = {}) {
    return new ChromeLocator(this, text, { ...options, kind: "text", text });
  }

  getByRole(role, options = {}) {
    return new ChromeLocator(this, role, { ...options, kind: "role", role });
  }

  getByLabel(text, options = {}) {
    return new ChromeLocator(this, text, { ...options, kind: "label", text });
  }

  getByPlaceholder(text, options = {}) {
    return new ChromeLocator(this, text, { ...options, kind: "placeholder", text });
  }

  getByTestId(testId, options = {}) {
    return new ChromeLocator(this, testId, { ...options, kind: "testId", testId });
  }

  describeApi() {
    return [
      "goto(url)",
      "reload()",
      "back()",
      "forward()",
      "close()",
      "title()",
      "url()",
      "screenshot(options)",
      "evaluate(fn, arg)",
      "domSnapshot(options)",
      "observe(options)",
      "locator(selector)",
      "content.text(options)",
      "content.html(options)",
      "content.read(options)",
      "content.get(options)",
      "playwright.domSnapshot(options)",
      "capabilities.list() returns []",
      "clipboard unsupported with explicit errors",
      "cua basic coordinate API",
      "dom_cua basic DOM node API",
    ];
  }
}

class ChromeTabsApi {
  constructor(browser) {
    this.browser = browser;
  }

  async new(options = {}) {
    const tab = await this.browser.client.request("tabs.new", options);
    return new ChromeTab(this.browser, tab);
  }

  async selected(options = {}) {
    const tab = await this.browser.client.request("tabs.selected", options);
    return new ChromeTab(this.browser, tab);
  }

  async list(options = {}) {
    return await this.browser.client.request("tabs.list", options);
  }

  async get(tab) {
    const result = await this.browser.client.request("tabs.get", { tab: tabReference(tab) });
    return new ChromeTab(this.browser, result);
  }

  async content(options = {}) {
    if (Array.isArray(options.urls) && options.urls.length > 0) {
      return await this.browser.client.request("tabs.content", options);
    }
    const tab = await this.selected(options);
    return await tab.content.read(options);
  }

  async read(options = {}) {
    return await this.content(options);
  }

  async finalize(options = {}) {
    return await this.browser.client.request("tabs.finalize", normalizeFinalizeOptions(options));
  }

  describeApi() {
    return ["new(options)", "selected(options)", "list(options)", "get(tab)", "content(options)", "read(options)", "finalize(options)"];
  }
}

class ChromeUserApi {
  constructor(browser) {
    this.browser = browser;
  }

  async openTabs(options = {}) {
    return await this.browser.client.request("user.openTabs", options);
  }

  async claimTab(tab, options = {}) {
    const result = await this.browser.client.request("user.claimTab", { tab: tabReference(tab), options });
    return new ChromeTab(this.browser, result);
  }

  async history(_options = {}) {
    throw new Error("Chrome history access is intentionally unavailable in DotCraft.");
  }

  describeApi() {
    return ["openTabs(options)", "claimTab(tab, options)", "history() intentionally unavailable"];
  }
}

class ChromeBrowser {
  constructor(client) {
    this.client = client;
    this.tabs = new ChromeTabsApi(this);
    this.user = new ChromeUserApi(this);
    this.capabilities = new EmptyCapabilityCollection();
  }

  async nameSession(name) {
    return await this.client.request("browser.nameSession", { name });
  }

  describeApi() {
    return {
      browser: ["nameSession(name)", "tabs", "user", "capabilities.list()"],
      tabs: this.tabs.describeApi(),
      user: this.user.describeApi(),
    };
  }
}

export async function setupAtlasRuntime(options = {}) {
  const globals = options.globals || globalThis;
  const backend = options.backend || "extension";

  if (backend !== "extension") {
    const fallbackPath = options.browserUseClientPath || globals.dotcraft?.browserUseClientPath;
    if (!fallbackPath) {
      throw new Error("No Browser Use client path is available for non-extension backend.");
    }
    const fallback = await import(fallbackPath);
    return await fallback.setupAtlasRuntime(options);
  }

  const bridgeOptions = options.chromeBridge || {};
  globals.agent = globals.agent || {};
  const existingBrowsers = globals.agent.browsers;
  const existingList = typeof existingBrowsers?.list === "function"
    ? existingBrowsers.list.bind(existingBrowsers)
    : null;
  const existingGet = typeof existingBrowsers?.get === "function"
    ? existingBrowsers.get.bind(existingBrowsers)
    : null;
  const existingDescribeApi = typeof existingBrowsers?.describeApi === "function"
    ? existingBrowsers.describeApi.bind(existingBrowsers)
    : null;

  globals.agent.browsers = {
    async list() {
      const existing = existingList ? await existingList() : [];
      const items = Array.isArray(existing) ? [...existing] : [];
      if (!items.some((item) => item?.id === "extension")) {
        items.push({ id: "extension", name: "DotCraft Chrome", type: "extension" });
      }
      return items;
    },
    async get(name = "extension") {
      if (name === "extension" || name === "chrome") {
        const client = new ChromeBridgeClient(bridgeOptions);
        await client.ensureConnected();
        return new ChromeBrowser(client);
      }
      if (existingGet) {
        return await existingGet(name);
      }
      throw new Error(`Unsupported browser backend: ${name}`);
    },
    describeApi: () => [
      ...(existingDescribeApi ? existingDescribeApi() : []),
      'get("extension")',
      'get("chrome")',
    ],
  };

  return globals.agent;
}
