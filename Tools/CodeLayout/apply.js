// Applies Tools/CodeLayout/moves.tsv: moves each file (git mv), gives it the namespace of its new
// folder, and rewrites what referred to the old namespaces: `using` lines, fully qualified names
// (in code, strings and comments), and file paths in the docs. Run from a clean tree, then build.
//   node Tools/CodeLayout/apply.js
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');

const repo = path.resolve(__dirname, '../..');
const modDir = path.join(repo, 'Multiplayer');
const BOM = '﻿';

// Windows can hold a just-moved file open for a moment (indexer, antivirus): retry briefly.
function retry(action) {
    for (let attempt = 0; ; attempt++) {
        try { return action(); }
        catch (e) {
            if (attempt >= 50 || !["UNKNOWN", "EBUSY", "EPERM", "EACCES"].includes(e.code) && !String(e.message).includes("Permission denied")) throw e;
            Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 100);
        }
    }
}
const read = file => retry(() => fs.readFileSync(file, "utf8"));
const write = (file, text) => retry(() => fs.writeFileSync(file, text));


const moves = fs.readFileSync(path.join(__dirname, 'moves.tsv'), 'utf8').split('\n')
    .filter(line => line && !line.startsWith('#')).map(line => {
        const [from, to] = line.split('\t');
        return { from, to };
    });

const nsOfFolder = rel => {
    const dir = path.posix.dirname(rel);
    return dir === '.' ? 'Multiplayer' : 'Multiplayer.' + dir.split('/').join('.');
};

// --- What each file declares, before anything moves.
const nsLine = /^[ \t]*namespace[ \t]+([A-Za-z_][\w.]*)/m;
const typeDecl = /^([ \t]*)(?:\[[^\]\n]*\][ \t]*)*(?:(?:public|internal|private|protected|static|sealed|abstract|partial|readonly|unsafe|new|ref)[ \t]+)*(?:class|struct|interface|enum|record(?:[ \t]+(?:class|struct))?|delegate[ \t]+[^(=;\n]+?)[ \t]+([A-Za-z_]\w*)/gm;

function declarations(text) {
    const body = text.replace(BOM, '');
    const m = body.match(nsLine);
    const ns = m ? m[1] : '';
    const block = m && !/^[ \t]*namespace[ \t]+[\w.]+[ \t]*;/m.test(body);
    const indent = block ? 4 : 0;
    const types = new Set();
    for (const t of body.matchAll(typeDecl)) {
        const width = t[1].replace(/\t/g, '    ').length;
        if (width === indent) types.add(t[2]);
    }
    return { ns, types };
}

const typeMap = new Map();      // `${oldNs}|${type}` -> newNs
const typesByNs = new Map();    // oldNs -> Set(types)
const oldNamespaces = new Set();
for (const move of moves) {
    const text = fs.readFileSync(path.join(modDir, move.from), 'utf8');
    const { ns, types } = declarations(text);
    move.oldNs = ns;
    move.newNs = nsOfFolder(move.to);
    oldNamespaces.add(ns);
    for (const type of types) {
        const key = `${ns}|${type}`;
        if (typeMap.has(key) && typeMap.get(key) !== move.newNs)
            throw new Error(`${type} (${ns}) would be in ${typeMap.get(key)} and ${move.newNs}`);
        typeMap.set(key, move.newNs);
        if (!typesByNs.has(ns)) typesByNs.set(ns, new Set());
        typesByNs.get(ns).add(type);
    }
}
const newNamespaces = new Set(moves.map(m => m.newNs));
// Old namespaces that no longer exist once everything has moved.
const goneNamespaces = new Set([...oldNamespaces].filter(ns => ns && !newNamespaces.has(ns)));

// --- Move.
const git = (...args) => retry(() => execFileSync('git', ['-C', repo, ...args], { encoding: 'utf8' }));
for (const move of moves) {
    if (move.from === move.to) continue;
    fs.mkdirSync(path.dirname(path.join(modDir, move.to)), { recursive: true });
    git('mv', path.posix.join('Multiplayer', move.from), path.posix.join('Multiplayer', move.to));
}

// --- Namespace declarations of the moved files.
const movedByNewPath = new Map(moves.map(m => [m.to, m]));
for (const move of moves) {
    const file = path.join(modDir, move.to);
    let text = read(file);
    const bom = text.startsWith(BOM);
    let body = bom ? text.slice(1) : text;
    if (move.oldNs) {
        if (move.oldNs !== move.newNs)
            body = body.replace(nsLine, line => line.replace(/namespace[ \t]+[\w.]+/, 'namespace ' + move.newNs));
    } else {
        // No namespace before: add a file-scoped one after the using directives.
        const lines = body.split('\n');
        let at = 0;
        lines.forEach((line, i) => { if (/^\s*using\s+[\w.=\s]+;/.test(line)) at = i + 1; });
        lines.splice(at, 0, ...(at ? [''] : []), `namespace ${move.newNs};`, '');
        body = lines.join('\n');
    }
    write(file, (bom ? BOM : '') + body);
}

// --- References, in every C# file that can see the mod.
const ancestors = ns => {
    const out = [''];
    const parts = ns ? ns.split('.') : [];
    for (let i = 1; i <= parts.length; i++) out.push(parts.slice(0, i).join('.'));
    return out;
};
const csFiles = dir => !fs.existsSync(dir) ? [] : fs.readdirSync(dir, { withFileTypes: true }).flatMap(e => {
    const full = path.join(dir, e.name);
    if (e.isDirectory()) return ['bin', 'obj', '.git', 'node_modules'].includes(e.name) ? [] : csFiles(full);
    return e.name.endsWith('.cs') ? [full] : [];
});
const roots = ['Multiplayer', 'Multiplayer.Protocol.Tests', 'DevTools', 'Tools', 'MultiplayerAPI Tests'];
const qualified = /(global::)?\bMultiplayer((?:\.[A-Za-z_]\w*)+)/g;

function rewriteQualified(text) {
    return text.replace(qualified, (whole, global, rest) => {
        const parts = rest.slice(1).split('.');
        // Longest old namespace prefix followed by a type declared in it.
        for (let n = parts.length - 1; n >= 0; n--) {
            const ns = ['Multiplayer', ...parts.slice(0, n)].join('.');
            const type = parts[n];
            const target = typeMap.get(`${ns}|${type}`);
            if (target)
                return (global || '') + [target, ...parts.slice(n)].join('.');
        }
        return whole;
    });
}

let changedFiles = 0;
for (const file of roots.flatMap(r => csFiles(path.join(repo, r)))) {
    const rel = path.relative(modDir, file).split(path.sep).join('/');
    const moved = movedByNewPath.get(rel);
    let text = read(file);
    const original = text;
    const bom = text.startsWith(BOM);
    let body = bom ? text.slice(1) : text;

    const ownOld = moved ? moved.oldNs : declarations(body).ns;
    const ownNew = moved ? moved.newNs : ownOld;
    const lines = body.split('\n');
    const usingRe = /^(\s*)using\s+([A-Za-z_][\w.]*)\s*;\s*$/;
    const explicit = [];
    lines.forEach((line, i) => { const m = line.match(usingRe); if (m) explicit.push({ i, ns: m[2] }); });

    const visible = new Set([...ancestors(ownOld), ...explicit.map(u => u.ns)]);
    const code = lines.filter((_, i) => !explicit.some(u => u.i === i)).join('\n');
    const needed = new Set();
    for (const ns of visible) {
        for (const type of typesByNs.get(ns) || []) {
            if (!new RegExp(`(?<![\\w.])${type}\\b`).test(code)) continue;
            const target = typeMap.get(`${ns}|${type}`);
            if (!ancestors(ownNew).includes(target)) needed.add(target);
        }
    }
    const keep = explicit.filter(u => !goneNamespaces.has(u.ns));
    for (const u of keep) needed.delete(u.ns);
    const drop = new Set(explicit.filter(u => goneNamespaces.has(u.ns)).map(u => u.i));
    if (drop.size || needed.size) {
        const at = drop.size ? Math.min(...drop) : (explicit.length ? Math.max(...explicit.map(u => u.i)) + 1 : 0);
        const indent = (explicit.find(u => drop.has(u.i)) && lines[Math.min(...drop)].match(/^\s*/)[0]) || '';
        const added = [...needed].sort().map(ns => `${indent}using ${ns};`);
        const out = [];
        lines.forEach((line, i) => {
            if (i === at) out.push(...added);
            if (!drop.has(i)) out.push(line);
        });
        if (at >= lines.length) out.push(...added);
        if (!explicit.length && added.length) out.splice(added.length, 0, '');
        body = out.join('\n');
    }
    body = rewriteQualified(body);
    text = (bom ? BOM : '') + body;
    if (text !== original) {
        write(file, text);
        changedFiles++;
    }
}

// --- `using <old namespace>;` written as text (dvmp eval `--usings` strings in scripts and C#
// string literals, examples in docs): every new namespace the old one's types went to.
const nsTargets = new Map();
for (const [key, target] of typeMap) {
    const ns = key.split('|')[0];
    if (!nsTargets.has(ns)) nsTargets.set(ns, new Set());
    nsTargets.get(ns).add(target);
}
const usingText = /using\s+(Multiplayer(?:\.[A-Za-z_]\w*)+)\s*;/g;
const scriptFiles = dir => !fs.existsSync(dir) ? [] : fs.readdirSync(dir, { withFileTypes: true }).flatMap(e => {
    const full = path.join(dir, e.name);
    if (e.isDirectory()) return ['bin', 'obj', '.git', 'node_modules'].includes(e.name) ? [] : scriptFiles(full);
    return /\.(cs|sh|js|md|ps1)$/.test(e.name) ? [full] : [];
});
let usingTextFiles = 0;
for (const file of ['DevTools', 'docs', 'Tools', 'Multiplayer.Protocol.Tests'].flatMap(r => scriptFiles(path.join(repo, r)))) {
    if (file.startsWith(__dirname)) continue;
    const original = read(file);
    const text = original.replace(usingText, (whole, ns) => {
        if (!goneNamespaces.has(ns)) return whole;
        return [...(nsTargets.get(ns) || [])].sort().map(t => `using ${t};`).join(' ');
    });
    if (text !== original) { write(file, text); usingTextFiles++; }
}
console.log(`using-as-text rewritten in ${usingTextFiles} files.`);

// --- Paths in the docs and comments: Multiplayer/<old> and the unique tails of old paths.
const tails = new Map();
for (const move of moves) {
    const parts = move.from.split('/');
    for (let i = 0; i < parts.length - 1; i++) {
        const tail = parts.slice(i).join('/');
        tails.set(tail, tails.has(tail) ? null : move);
    }
}
const pathPairs = [...tails].filter(([, move]) => move && move.from !== move.to)
    .sort((a, b) => b[0].length - a[0].length);
const textFiles = dir => !fs.existsSync(dir) ? [] : fs.readdirSync(dir, { withFileTypes: true }).flatMap(e => {
    const full = path.join(dir, e.name);
    if (e.isDirectory()) return ['bin', 'obj', '.git', 'node_modules', 'wiki-output', 'mpapi', 'build'].includes(e.name) ? [] : textFiles(full);
    return /\.(md|cs|sh|ps1|js|json)$/.test(e.name) && e.name !== 'moves.md' && e.name !== 'CHANGELOG.md' ? [full] : [];
});
const docRoots = ['docs', 'DevTools', 'Multiplayer', 'Tools', 'Multiplayer.Protocol.Tests'].map(r => path.join(repo, r))
    .concat(['README.md', 'CLAUDE.md'].map(f => path.join(repo, f)).filter(f => fs.existsSync(f)));
let pathFiles = 0;
for (const file of docRoots.flatMap(r => fs.statSync(r).isDirectory() ? textFiles(r) : [r])) {
    if (file.startsWith(__dirname)) continue;
    const original = read(file);
    let text = original;
    for (const [tail, move] of pathPairs) {
        const escaped = tail.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
        text = text.replace(new RegExp(`(?<![\\w/.-])(Multiplayer/)?${escaped}(?![\\w/])`, 'g'), (m, prefix) => (prefix || '') + move.to);
    }
    if (text !== original) { write(file, text); pathFiles++; }
}

console.log(`Moved ${moves.filter(m => m.from !== m.to).length} files; references rewritten in ${changedFiles} C# files; paths in ${pathFiles} files.`);
console.log(`Old namespaces gone: ${goneNamespaces.size}. New namespaces: ${newNamespaces.size}.`);
