// Smoke test: parse frontend/index.html, run the inline <script> in a
// minimal DOM stub, and fail if init throws or handlers aren't wired.
// node --check only validates syntax; this catches runtime init errors
// like $("missing-id").onclick = ... (null.onclick) that kill the page.
"use strict";

const fs = require("fs");
const path = require("path");
const vm = require("vm");

const frontendDir = path.join(__dirname, "..", "frontend");
const html = fs.readFileSync(path.join(frontendDir, "index.html"), "utf8");
const libSrc = fs.readFileSync(path.join(frontendDir, "lib.js"), "utf8");

// ids declared in the HTML
const ids = new Set([...html.matchAll(/\bid="([^"]+)"/g)].map((m) => m[1]));

class Elem {
  constructor(id) {
    this.id = id;
    this.children = [];
    this.value = "";
    this.checked = false;
    this.hidden = false;
    this.disabled = false;
    this.title = "";
    this.textContent = "";
    this.innerHTML = "";
    this.className = "";
    this.type = "";
    this.onclick = null;
    this.onchange = null;
    this.onkeydown = null;
    this.classList = {
      add() {},
      remove() {},
      toggle() {},
      contains() { return false; }
    };
  }
  appendChild(c) { this.children.push(c); return c; }
  removeChild(c) { this.children = this.children.filter((x) => x !== c); }
  setAttribute() {}
  addEventListener() {}
  focus() {}
  querySelectorAll() { return []; }
}

const byId = new Map();
for (const id of ids) byId.set(id, new Elem(id));

const documentStub = {
  getElementById: (id) => byId.get(id) || null,
  createElement: (tag) => new Elem(tag),
  addEventListener: () => {},
  querySelectorAll: () => []
};

const windowStub = {
  document: documentStub,
  addEventListener: () => {},
  console,
  __TAURI__: {
    core: {
      invoke: (cmd) => {
        if (cmd === "recent_list") return Promise.resolve([]);
        if (cmd === "open_db") return Promise.resolve({ ok: true, type: "collections", collections: [], count: 0 });
        if (cmd === "run_query") return Promise.resolve({ ok: true, type: "query", collection: "t", columns: [], count: 0, rows: [], elapsed_ms: 1 });
        return Promise.resolve({ ok: true });
      }
    },
    dialog: { open: () => Promise.resolve(null) }
  }
};
windowStub.window = windowStub;

const ctx = vm.createContext({
  window: windowStub,
  document: documentStub,
  console,
  setTimeout,
  Promise,
  JSON,
  Math,
  Date,
  Error,
  TypeError
});

let initError = null;
try {
  vm.runInContext(libSrc, ctx, { filename: "lib.js" });
  const inline = [...html.matchAll(/<script>([\s\S]*?)<\/script>/g)].map((m) => m[1]).join("\n");
  vm.runInContext(inline, ctx, { filename: "index.html inline script" });
} catch (e) {
  initError = e;
}

// let async init (recent_list) settle
setTimeout(() => {
  const problems = [];
  if (initError) {
    problems.push("inline script threw at init: " + String(initError && initError.stack || initError).split("\n").slice(0, 4).join(" | "));
  }
  // handlers that must exist after init
  const handlerChecks = [
    ["open-db", "onclick"],
    ["empty-open", "onclick"],
    ["conn-close", "onclick"],
    ["edit-connection", "onclick"],
    ["open-recent", "onclick"],
    ["recent-clear", "onclick"],
    ["connect-browse", "onclick"],
    ["connect-open", "onclick"],
    ["connect-close", "onclick"],
    ["connect-cancel", "onclick"],
    ["connect-backdrop", "onclick"],
    ["connect-show", "onclick"],
    ["connect-clear-recent", "onclick"],
    ["modal-close", "onclick"],
    ["modal-cancel", "onclick"],
    ["modal-save", "onclick"],
    ["modal-backdrop", "onclick"]
  ];
  for (const [id, prop] of handlerChecks) {
    if (!ids.has(id)) {
      problems.push(`missing id="${id}" in HTML`);
    } else if (!byId.get(id)[prop]) {
      problems.push(`#${id} has no ${prop} — init likely aborted before wiring handlers`);
    }
  }
  if (problems.length) {
    console.error("index.html smoke test FAILED:");
    for (const p of problems) console.error("  - " + p);
    process.exit(1);
  }
  console.log("index.html smoke test passed");
}, 50);
