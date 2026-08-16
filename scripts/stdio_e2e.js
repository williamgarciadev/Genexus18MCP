#!/usr/bin/env node
// End-to-end smoke for the Gateway over MCP stdio. Spawns the published exe,
// drives the protocol, validates the index-UX changes (#4 fast-path, #5 partial
// results, #6 progress notifications, #7 cold-start notice + filter).
//
// Usage:
//   node scripts/stdio_e2e.js
//   (reads publish/config.json — KBPath must point at a real KB)

const { spawn } = require('child_process');
const path = require('path');
const fs = require('fs');

const root = path.resolve(__dirname, '..');
const exe = path.join(root, 'publish', 'GxMcp18.Gateway.exe');
const cfgPath = path.join(root, 'publish', 'config.json');

if (!fs.existsSync(exe)) { console.error('publish exe missing — run .\\build.ps1 first'); process.exit(2); }
const cfg = JSON.parse(fs.readFileSync(cfgPath, 'utf8'));
const kbPath = cfg.Environment?.KBPath;
console.error(`[harness] KB = ${kbPath}`);

const child = spawn(exe, [], { cwd: path.dirname(exe), stdio: ['pipe', 'pipe', 'pipe'] });

const inbox = [];
const waiters = [];
const pending = new Map();
let nextId = 1;
let buffer = '';

child.stdout.on('data', chunk => {
    buffer += chunk.toString('utf8');
    let nl;
    while ((nl = buffer.indexOf('\n')) >= 0) {
        const line = buffer.slice(0, nl).trim();
        buffer = buffer.slice(nl + 1);
        if (!line || !line.startsWith('{')) continue;
        let msg;
        try { msg = JSON.parse(line); } catch { continue; }
        if (msg.id !== undefined && pending.has(msg.id)) {
            pending.get(msg.id)(msg);
            pending.delete(msg.id);
        } else if (msg.method) {
            inbox.push(msg);
            const w = waiters.shift();
            if (w) w(msg);
        }
    }
});

child.stderr.on('data', c => process.stderr.write('[gw-err] ' + c.toString()));
child.on('exit', (code, sig) => console.error(`[harness] gateway exit code=${code} sig=${sig}`));

function send(method, params) {
    return new Promise((resolve) => {
        const id = nextId++;
        pending.set(id, resolve);
        const env = { jsonrpc: '2.0', id, method, params: params || {} };
        child.stdin.write(JSON.stringify(env) + '\n');
    });
}

function sendNotification(method, params) {
    const env = { jsonrpc: '2.0', method, params: params || {} };
    child.stdin.write(JSON.stringify(env) + '\n');
}

function check(label, cond, detail) {
    const tag = cond ? 'PASS' : 'FAIL';
    console.error(`[${tag}] ${label}${detail ? ' — ' + detail : ''}`);
    return cond;
}

async function main() {
    let allOk = true;
    await new Promise(r => setTimeout(r, 800));

    const initResp = await send('initialize', {
        protocolVersion: '2024-11-05',
        capabilities: {},
        clientInfo: { name: 'stdio-e2e', version: '0.1' }
    });
    allOk &= check('initialize returns result', !!initResp.result, JSON.stringify(initResp).slice(0, 150));

    sendNotification('notifications/initialized', {});

    // Snapshot notifications received in the next 12s — cold-start path emits
    // the indexing notice ~4-5s after initialize (worker has to open the KB
    // first) and first progress event lands a few seconds later. Warm cache
    // returns "AlreadyIndexed" much faster.
    await new Promise(r => setTimeout(r, 12000));
    const opSpam = inbox.filter(m =>
        m.method === 'notifications/message' &&
        /Operation .* (started|finished)/i.test(m.params?.data || ''));
    allOk &= check('no operational spam on stdio', opSpam.length === 0,
        opSpam.length ? `leaked ${opSpam.length}: ${JSON.stringify(opSpam[0].params).slice(0, 120)}` : '');

    const warmupLeaks = inbox.filter(m =>
        m.method === 'notifications/message' &&
        /Worker warmup/i.test(m.params?.data || ''));
    allOk &= check('no warmup spam on stdio', warmupLeaks.length === 0);

    // notifications/progress should pass through whenever indexing is happening.
    // On a warm cache the bootstrap returns "AlreadyIndexed" and emits none —
    // that's OK. Just record what we saw.
    const progressMsgs = inbox.filter(m => m.method === 'notifications/progress');
    console.error(`[info] progress notifications received: ${progressMsgs.length}`);
    if (progressMsgs[0]) console.error('[info] sample progress: ' + JSON.stringify(progressMsgs[0].params).slice(0, 200));

    const indexingNotices = inbox.filter(m =>
        m.method === 'notifications/message' &&
        m.params?.logger === 'indexing');
    console.error(`[info] indexing notices: ${indexingNotices.length}`);
    if (indexingNotices[0]) console.error('[info] notice: ' + JSON.stringify(indexingNotices[0].params).slice(0, 250));

    // Tool call: literal-name query. Must come back FAST regardless of index state
    // (this is the fast-path).
    const probeName = 'Country';
    const t0 = Date.now();
    const queryResp = await send('tools/call', {
        name: 'genexus_query',
        arguments: { query: probeName, limit: 5 }
    });
    const elapsed = Date.now() - t0;
    let payload = null;
    try { payload = JSON.parse(queryResp.result?.content?.[0]?.text || '{}'); } catch {}
    const directHit = payload?._meta?.direct_lookup === true;
    const partialMeta = payload?._meta?.partial === true;
    console.error('[info] query payload: ' + JSON.stringify(payload).slice(0, 250));
    allOk &= check(`genexus_query literal returned in <5s`, elapsed < 5000, `${elapsed}ms`);
    allOk &= check('result has results array', Array.isArray(payload?.results));
    // direct_lookup OR partial both acceptable (depends on cache state).
    allOk &= check('_meta signals state (direct_lookup or partial)', directHit || partialMeta || (payload?.count > 0));

    // Tool call: fuzzy pattern. Should NOT match direct lookup; if index is warming
    // we expect _meta.partial=true. If index is warm we just get normal results.
    const fuzzyResp = await send('tools/call', {
        name: 'genexus_query',
        arguments: { query: 'cust', limit: 3 }
    });
    let fuzzyPayload = null;
    try { fuzzyPayload = JSON.parse(fuzzyResp.result?.content?.[0]?.text || '{}'); } catch {}
    console.error('[info] fuzzy payload: ' + JSON.stringify(fuzzyPayload).slice(0, 250));
    allOk &= check('fuzzy returned a structured payload', !!fuzzyPayload);
    // No assertion on partial vs warm — just observe.

    // Wait a bit longer so the first notifications/progress event has time to land
    // (cold start: ~8s snapshot phase, then first checkpoint at 500 processed objects).
    await new Promise(r => setTimeout(r, 20000));
    const progressTotal = inbox.filter(m => m.method === 'notifications/progress').length;
    console.error(`[info] progress notifications received (after wait): ${progressTotal}`);
    const lastProgress = inbox.filter(m => m.method === 'notifications/progress').pop();
    if (lastProgress) console.error('[info] last progress: ' + JSON.stringify(lastProgress.params));
    allOk &= check('progress notifications fired on cold start', progressTotal > 0 || /* warm cache acceptable: */ inbox.some(m => m.method === 'notifications/message' && m.params?.logger === 'indexing') === false);

    child.stdin.end();
    setTimeout(() => child.kill(), 500);

    console.error('---');
    console.error(allOk ? '[harness] ALL CHECKS PASSED' : '[harness] CHECKS FAILED');
    process.exit(allOk ? 0 : 1);
}

main().catch(e => { console.error('[harness] error', e); child.kill(); process.exit(2); });
