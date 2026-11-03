// The Taipan extension (SPEC §16.8): starts `taipan lsp --stdio` as the client for every Taipan document.
const vscode = require('vscode');
const { LanguageClient } = require('vscode-languageclient/node');

let client;

function activate() {
    const command = vscode.workspace.getConfiguration('taipan').get('path', 'taipan');
    const server = { command, args: ['lsp', '--stdio'] };
    client = new LanguageClient('taipan', 'Taipan', { run: server, debug: server }, {
        documentSelector: [{ scheme: 'file', language: 'taipan' }],
    });
    return client.start();
}

function deactivate() {
    return client?.stop();
}

module.exports = { activate, deactivate };
