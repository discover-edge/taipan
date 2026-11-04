// @ts-check
import { readFileSync } from 'node:fs';
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

// Where the site is served: the custom domain set in the repository's Pages settings. Pages link
// to each other with relative links, so they work under any base path too.
const site = 'https://taipan-lang.org';
const base = '/';
const repository = 'https://github.com/taipan-org/taipan';

// Taipan is highlighted with the VS Code extension's grammar (SPEC §16.8), and a sample's
// output (an `output` or `error` block) as plain text.
const taipan = JSON.parse(readFileSync(new URL('../editors/vscode/syntaxes/taipan.tmLanguage.json', import.meta.url), 'utf8'));
const plain = (name) => ({ name, scopeName: `text.${name}`, patterns: [] });

export default defineConfig({
	site,
	base,
	trailingSlash: 'always',
	integrations: [
		starlight({
			title: 'Taipan',
			description: 'A statically typed, compiled language with Python’s syntax, for .NET.',
			logo: { src: './src/assets/logo.svg' },
			favicon: '/favicon.svg',
			social: [{ icon: 'github', label: 'GitHub', href: repository }],
			editLink: { baseUrl: `${repository}/edit/main/website/` },
			customCss: ['./src/styles/custom.css'],
			components: { Footer: './src/components/Footer.astro' },
			expressiveCode: {
				shiki: { langs: [{ ...taipan, name: 'taipan', aliases: ['tpn'] }, plain('output'), plain('error'), plain('ebnf')] },
			},
			sidebar: [
				{ label: 'Get started', items: [{ autogenerate: { directory: 'start' } }] },
				{ label: 'Tour of Taipan', items: [{ autogenerate: { directory: 'tour' } }] },
				{ label: 'Guides', items: [{ autogenerate: { directory: 'guides' } }] },
				{ label: 'Reference', items: [{ autogenerate: { directory: 'reference' } }] },
				{ label: 'Diagnostics', collapsed: true, items: [{ autogenerate: { directory: 'diagnostics' } }] },
			],
		}),
	],
});
