// Prints the tokens the TextMate grammar gives a file (SPEC §16.8), one per line:
// `<line>:<column> <text as JSON> <scopes after source.taipan, or ->`. Whitespace-only tokens are left out.
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const vsctm = require('vscode-textmate');
const oniguruma = require('vscode-oniguruma');

const here = path.dirname(fileURLToPath(import.meta.url));
const grammarPath = path.join(here, '..', 'syntaxes', 'taipan.tmLanguage.json');
const wasm = fs.readFileSync(require.resolve('vscode-oniguruma/release/onig.wasm'));
await oniguruma.loadWASM(wasm.buffer.slice(wasm.byteOffset, wasm.byteOffset + wasm.byteLength));

const registry = new vsctm.Registry({
    onigLib: Promise.resolve({
        createOnigScanner: patterns => new oniguruma.OnigScanner(patterns),
        createOnigString: text => new oniguruma.OnigString(text),
    }),
    loadGrammar: async scope => scope === 'source.taipan' ? vsctm.parseRawGrammar(fs.readFileSync(grammarPath, 'utf8'), grammarPath) : null,
});
const grammar = await registry.loadGrammar('source.taipan');

const lines = fs.readFileSync(process.argv[2], 'utf8').replace(/\r\n/g, '\n').split('\n');
if (lines.at(-1) === '')
    lines.pop();
let state = vsctm.INITIAL;
const out = [];
lines.forEach((line, i) => {
    const result = grammar.tokenizeLine(line, state);
    for (const token of result.tokens) {
        const text = line.slice(token.startIndex, token.endIndex);
        if (text.trim() === '')
            continue;
        const scopes = token.scopes.slice(1);
        out.push(`${i + 1}:${token.startIndex + 1} ${JSON.stringify(text)} ${scopes.length ? scopes.join(' ') : '-'}`);
    }
    state = result.ruleStack;
});
process.stdout.write(out.map(l => l + '\n').join(''));
