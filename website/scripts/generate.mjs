// Writes the pages made from the repository rather than by hand: the specification (SPEC.md as
// it stands) and a page for every diagnostic code in SPEC §9, each with its failing golden case.
import { existsSync, mkdirSync, readdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { basename, dirname, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const docs = join(root, 'website', 'src', 'content', 'docs');
const repository = 'https://github.com/taipan-org/taipan';
const spec = readFileSync(join(root, 'SPEC.md'), 'utf8').replace(/\r\n/g, '\n');

// ---- The specification ----

// SPEC.md's own title is the page's, and its Taipan samples are fenced as `python`.
const body = spec.replace(/^# .*\n/, '').replace(/^```python$/gm, '```taipan');
writeFileSync(join(docs, 'reference', 'specification.md'), `---
title: Language specification
description: The complete definition of Taipan, from SPEC.md in the repository.
tableOfContents:
  maxHeadingLevel: 3
editUrl: ${repository}/edit/main/SPEC.md
sidebar:
  order: 100
---

:::note
This is [SPEC.md](${repository}/blob/main/SPEC.md), the source of truth for the language. The compiler implements it, and the rest of this site explains it.
:::
${body}`);

// ---- Diagnostics ----

const section = spec.slice(spec.indexOf('\n## 9. Diagnostics'), spec.indexOf('\n## 10. '));

// A table row's cells, splitting on `|` but not `\|`.
const cells = (row) => row.slice(1, -1).split(/(?<!\\)\|/).map((c) => c.trim().replace(/\\\|/g, '|'));

// The rows of the table whose header is `| Code | <column> |`, by code.
function table(column) {
	const rows = new Map();
	const start = section.indexOf(`| Code | ${column} |`);
	for (const line of section.slice(start).split('\n').slice(2)) {
		if (!line.startsWith('| TP')) break;
		const [code, text] = cells(line);
		rows.set(code, [...(rows.get(code) ?? []), text]);
	}
	return rows;
}

// A message cell lists its messages separated by ` / `, outside code and parentheses.
function messages(cell) {
	const parts = [];
	let [depth, code, current] = [0, false, ''];
	for (let i = 0; i < cell.length; i++) {
		const c = cell[i];
		if (c === '`') code = !code;
		else if (!code && c === '(') depth++;
		else if (!code && c === ')') depth--;
		if (!code && depth === 0 && cell.startsWith(' / ', i)) {
			parts.push(current);
			current = '';
			i += 2;
			continue;
		}
		current += c;
	}
	return [...parts, current].map((p) => p.trim()).filter(Boolean);
}

const meanings = table('Meaning');
// The SDK's codes' messages are in §18.5's table, `| \`TPN001\` | \`<message>\` |`.
const sdkMessages = new Map([...spec.matchAll(/^\| `(TPN\d+)` \| (.*) \|$/gm)].map((m) => [m[1], [`${m[2]}, reported by \`dotnet build\``]]));
const messageRows = table('Message');

// Every golden case, with its stderr: single files, and multi-file cases' main.tpn.
function cases(directory) {
	const found = [];
	const entries = readdirSync(directory);
	const isCase = entries.includes('main.tpn');
	for (const entry of entries) {
		const path = join(directory, entry);
		if (statSync(path).isDirectory()) found.push(...cases(path));
		else if (entry.endsWith('.tpn') && (!isCase || entry === 'main.tpn')) found.push(path);
	}
	return found;
}
const sidecar = (file, extension) => file.replace(/\.tpn$/, extension);
const golden = cases(join(root, 'tests', 'cases'))
	.filter((file) => existsSync(sidecar(file, '.err')))
	.map((file) => ({
		file,
		path: relative(root, file).replaceAll('\\', '/'),
		source: readFileSync(file, 'utf8').replace(/\r\n/g, '\n'),
		err: readFileSync(sidecar(file, '.err'), 'utf8').replace(/\r\n/g, '\n'),
		multiFile: basename(file) === 'main.tpn',
		python: existsSync(sidecar(file, '.requires')),
	}));

// The case that shows a code best: one named for it, on its own file, short, giving few errors.
function example(code) {
	const candidates = golden.filter((c) => new RegExp(`(error|warning) ${code}:`).test(c.err));
	const score = (c) =>
		(basename(c.file).startsWith(code.toLowerCase()) ? 0 : 10_000) +
		(c.multiFile ? 5_000 : 0) +
		(c.python ? 1_000 : 0) +
		c.source.length +
		c.err.split('\n').length * 40;
	return candidates.sort((a, b) => score(a) - score(b))[0];
}

const fence = (text) => '`'.repeat(Math.max(3, ...[...text.matchAll(/`+/g)].map((m) => m[0].length + 1)));
const block = (lang, text) => `${fence(text)}${lang}\n${text.replace(/\n$/, '')}\n${fence(text)}`;
const plainMeaning = (meaning) => meaning.replace(/\s*\((M[\d.]+[^)]*|since M[^)]*)\)/g, '');

const out = join(docs, 'diagnostics');
rmSync(out, { recursive: true, force: true });
mkdirSync(out, { recursive: true });

const intro = section.slice(section.indexOf('**Format:**'), section.indexOf('| Code | Meaning |')).trim();
const index = [];
for (const [code, rows] of meanings) {
	const meaning = rows.join('; ');
	const warning = /^warning/.test(meaning) || ['TP0300', 'TP0301', 'TP0302', 'TP0303'].includes(code);
	// The SDK's own codes (TPN…) are reported by `dotnet build`, so they have no golden case.
	const build = code.startsWith('TPN');
	const shown = build ? null : example(code);
	if (!build && !shown) throw new Error(`${code} has no failing golden case in tests/cases`);
	const listed = build ? sdkMessages.get(code) ?? [] : (messageRows.get(code) ?? []).flatMap(messages);
	const page = [
		'---',
		`title: "${code}"`,
		`description: ${JSON.stringify(`${warning ? 'Warning' : 'Error'} ${code}: ${plainMeaning(meaning).replace(/`/g, '')}`)}`,
		`editUrl: ${repository}/edit/main/SPEC.md`,
		'---',
		'',
		`**${warning ? 'Warning' : 'Error'}:** ${meaning}`,
		'',
		...(listed.length ? ['## Messages', '', ...listed.map((m) => `- ${m}`), ''] : []),
		...(shown ? exampleSection(shown) : []),
		`The [specification](../../reference/specification/#9-diagnostics) lists every code.`,
		'',
	];
	writeFileSync(join(out, `${code.toLowerCase()}.md`), page.join('\n'));
	index.push(`| [${code}](${code.toLowerCase()}/) | ${warning ? 'warning' : 'error'} | ${plainMeaning(meaning)} |`);
}

function exampleSection(shown) {
	const name = basename(shown.file);
	return [
		'## Example',
		'',
		`From the golden case [\`${shown.path}\`](${repository}/blob/main/${shown.path})${shown.multiFile ? ' (one file of a multi-file case)' : ''}:`,
		'',
		block(`taipan title="${name}"`, shown.source),
		'',
		"The compiler reports:",
		'',
		block('error', shown.err.replaceAll(shown.path, name)),
		'',
	];
}

writeFileSync(join(out, 'index.md'), `---
title: Diagnostics
description: Every error and warning the Taipan compiler reports, with an example of each.
editUrl: ${repository}/edit/main/SPEC.md
sidebar:
  label: All codes
  order: 0
---

The compiler reports problems as diagnostics, each with a code. Every code has a page with its messages and a program that gives it.

${intro}

| Code | Kind | Meaning |
|---|---|---|
${index.join('\n')}
`);

console.log(`generated the specification and ${meanings.size} diagnostic pages`);
