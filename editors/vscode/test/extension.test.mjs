// The extension's scripted test (SPEC §16.8): runs extension.js under Node with stand-ins for the
// `vscode` module and the LSP client, and checks its commands, the server it starts, and the
// message a missing `taipan` gives. `node test/extension.test.mjs` prints `ok` when they pass.
import assert from 'node:assert/strict';
import Module, { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const here = path.dirname(fileURLToPath(import.meta.url));
const workspace = path.join(here, 'workspace');
const file = path.join(workspace, 'src', 'main.tpn');

// What the extension did, for the checks to read.
let events;
const terminals = [];
let settings;
let answer;

class Terminal {
    constructor(options) { this.options = options; this.disposed = false; terminals.push(this); events.push(['terminal', options]); }
    show() { events.push(['show', this.options.name]); }
    sendText(text) { events.push(['send', text]); }
    dispose() { this.disposed = true; }
}

const commands = new Map();
const vscode = {
    workspace: {
        getConfiguration: section => ({ get: (key, fallback) => settings[`${section}.${key}`] ?? fallback }),
        getWorkspaceFolder: uri => uri.fsPath.startsWith(workspace) ? { uri: { fsPath: workspace } } : undefined,
        onDidChangeConfiguration: listener => { vscode.workspace.changed = listener; return { dispose() {} }; },
    },
    window: {
        activeTextEditor: undefined,
        createTerminal: options => new Terminal(typeof options === 'string' ? { name: options } : options),
        showErrorMessage: (message, ...items) => { events.push(['error', message, items]); return Promise.resolve(answer); },
        onDidCloseTerminal: () => ({ dispose() {} }),
    },
    commands: {
        registerCommand: (name, callback) => { commands.set(name, callback); return { dispose() {} }; },
        executeCommand: (...args) => { events.push(['command', ...args]); return Promise.resolve(); },
    },
};

class LanguageClient {
    constructor(id, name, server) { this.server = server; events.push(['client', server.run.command, server.run.args]); }
    start() { events.push(['start']); return Promise.resolve(); }
    stop() { events.push(['stop']); return Promise.resolve(); }
}

const load = Module._load;
Module._load = function (request, ...rest) {
    if (request === 'vscode')
        return vscode;
    if (request === 'vscode-languageclient/node')
        return { LanguageClient };
    return load.call(this, request, ...rest);
};
const extension = require('../extension.js');

// A command that exists everywhere and prints a version: Node itself.
const present = process.execPath;
const missing = path.join(here, 'no-such-taipan');
const tick = () => new Promise(resolve => setImmediate(resolve));
// Waits for the events to reach a length, for work the extension doesn't return a promise for.
async function settle(length) {
    for (let waited = 0; events.length < length && waited < 10_000; waited += 10)
        await new Promise(resolve => setTimeout(resolve, 10));
}

async function activate(taipan) {
    events = [];
    settings = { 'taipan.path': taipan };
    commands.clear();
    await extension.activate({ subscriptions: [] });
}

// Activating registers both commands and starts the server.
await activate(present);
assert.deepEqual([...commands.keys()].sort(), ['taipan.restartServer', 'taipan.runFile']);
assert.deepEqual(events, [['client', present, ['lsp', '--stdio']], ['start']]);

// Run File saves the file and runs it in a terminal of its own, in its workspace folder.
let saved = false;
vscode.window.activeTextEditor = { document: { languageId: 'taipan', uri: { scheme: 'file', fsPath: file }, save: async () => (saved = true) } };
events = [];
await commands.get('taipan.runFile')();
assert.ok(saved);
assert.deepEqual(events, [
    ['terminal', { name: 'Taipan', shellPath: present, shellArgs: ['run', file], cwd: workspace }],
    ['show', 'Taipan'],
]);

// Outside a workspace it runs in the file's directory, and replaces the terminal it opened before.
const outside = path.join(here, 'elsewhere', 'main.tpn');
vscode.window.activeTextEditor.document.uri.fsPath = outside;
events = [];
await commands.get('taipan.runFile')();
assert.equal(events[0][1].cwd, path.dirname(outside));
assert.equal(terminals.length, 2);
assert.ok(terminals[0].disposed && !terminals[1].disposed);

// It runs only Taipan files.
vscode.window.activeTextEditor = { document: { languageId: 'python', uri: { scheme: 'file', fsPath: file }, save: async () => true } };
events = [];
await commands.get('taipan.runFile')();
assert.deepEqual(events, []);

// Restart Language Server stops the client and starts a new one.
events = [];
await commands.get('taipan.restartServer')();
assert.deepEqual(events, [['stop'], ['client', present, ['lsp', '--stdio']], ['start']]);

// Changing taipan.path restarts it too; other settings don't.
events = [];
vscode.workspace.changed({ affectsConfiguration: section => section === 'editor.tabSize' });
await tick();
assert.deepEqual(events, []);
vscode.workspace.changed({ affectsConfiguration: section => section === 'taipan.path' });
await settle(3);
assert.deepEqual(events, [['stop'], ['client', present, ['lsp', '--stdio']], ['start']]);
await extension.deactivate();

// A taipan.path that doesn't exist gives the message, and starts no server.
const message = `Taipan: '${missing}' wasn't found, so the language server can't start. Install Taipan with 'dotnet tool install -g TaipanLang', or set 'taipan.path'.`;
answer = 'Install';
await activate(missing);
await tick();
assert.deepEqual(events, [
    ['error', message, ['Install', 'Open Settings']],
    ['terminal', { name: 'Taipan: install' }],
    ['show', 'Taipan: install'],
    ['send', 'dotnet tool install -g TaipanLang'],
]);

answer = 'Open Settings';
await activate(missing);
await tick();
assert.deepEqual(events.slice(1), [['command', 'workbench.action.openSettings', 'taipan.path']]);

answer = undefined;
await activate(missing);
await tick();
assert.deepEqual(events, [['error', message, ['Install', 'Open Settings']]]);
await extension.deactivate();

console.log('ok');
