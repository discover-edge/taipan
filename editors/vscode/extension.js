// The Taipan extension (SPEC §16.8): starts `taipan lsp --stdio` as the client for every Taipan
// document, and adds the commands Run File and Restart Language Server.
const path = require('path');
const { execFile } = require('child_process');
const vscode = require('vscode');
const { LanguageClient } = require('vscode-languageclient/node');

const install = 'dotnet tool install -g TaipanLang';

let client;
let runTerminal;

function taipanPath() {
    return vscode.workspace.getConfiguration('taipan').get('path', 'taipan');
}

// Whether `command` can be started at all: a command that doesn't exist fails with ENOENT. Other
// failures are left to the LSP client to report.
function found(command) {
    return new Promise(resolve => execFile(command, ['--version'], error => resolve(error?.code !== 'ENOENT')));
}

// The message isn't waited for, so activation doesn't wait for it to be answered.
function reportMissing(command) {
    vscode.window.showErrorMessage(
        `Taipan: '${command}' wasn't found, so the language server can't start. Install Taipan with '${install}', or set 'taipan.path'.`,
        'Install', 'Open Settings',
    ).then(choice => {
        if (choice === 'Install') {
            const terminal = vscode.window.createTerminal('Taipan: install');
            terminal.show();
            terminal.sendText(install);
        } else if (choice === 'Open Settings') {
            vscode.commands.executeCommand('workbench.action.openSettings', 'taipan.path');
        }
    });
}

async function start() {
    const command = taipanPath();
    if (!await found(command)) {
        reportMissing(command);
        return;
    }
    const server = { command, args: ['lsp', '--stdio'] };
    client = new LanguageClient('taipan', 'Taipan', { run: server, debug: server }, {
        documentSelector: [{ scheme: 'file', language: 'taipan' }],
    });
    await client.start();
}

async function stop() {
    const stopping = client;
    client = undefined;
    await stopping?.stop();
}

async function restart() {
    await stop();
    await start();
}

// Runs the active Taipan file in a terminal of its own, as the terminal's process rather than
// through a shell, so no path needs quoting.
async function runFile() {
    const document = vscode.window.activeTextEditor?.document;
    if (document?.languageId !== 'taipan' || document.uri.scheme !== 'file')
        return;
    if (!await document.save())
        return;
    const file = document.uri.fsPath;
    const folder = vscode.workspace.getWorkspaceFolder(document.uri);
    runTerminal?.dispose();
    runTerminal = vscode.window.createTerminal({
        name: 'Taipan',
        shellPath: taipanPath(),
        shellArgs: ['run', file],
        cwd: folder ? folder.uri.fsPath : path.dirname(file),
    });
    runTerminal.show();
}

function activate(context) {
    context.subscriptions.push(
        vscode.commands.registerCommand('taipan.runFile', runFile),
        vscode.commands.registerCommand('taipan.restartServer', restart),
        vscode.workspace.onDidChangeConfiguration(event => {
            if (event.affectsConfiguration('taipan.path'))
                restart();
        }),
        vscode.window.onDidCloseTerminal(terminal => {
            if (terminal === runTerminal)
                runTerminal = undefined;
        }),
    );
    return start();
}

function deactivate() {
    return stop();
}

module.exports = { activate, deactivate };
