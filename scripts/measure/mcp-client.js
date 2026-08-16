// Cliente MCP minimo sobre Streamable HTTP para la medicion del paso 2.
// Uso: node mcp.js <tool> '<jsonArgs>'   |   node mcp.js --open
const BASE = process.env.MCP_URL || 'http://127.0.0.1:5001/mcp';
let SESSION = null;

async function rpc(method, params, id) {
  const headers = {
    'Content-Type': 'application/json',
    'Accept': 'application/json, text/event-stream'
  };
  if (SESSION) headers['MCP-Session-Id'] = SESSION;

  const res = await fetch(BASE, {
    method: 'POST',
    headers,
    body: JSON.stringify({ jsonrpc: '2.0', id, method, params })
  });

  const sid = res.headers.get('mcp-session-id');
  if (sid) SESSION = sid;

  const text = await res.text();
  // La respuesta puede venir como SSE (frames "data: {...}")
  if (text.startsWith('event:') || text.includes('\ndata: ') || text.startsWith('data: ')) {
    const line = text.split('\n').find(l => l.startsWith('data: '));
    return line ? JSON.parse(line.slice(6)) : { raw: text };
  }
  try { return JSON.parse(text); } catch { return { raw: text, status: res.status }; }
}

(async () => {
  await rpc('initialize', {
    protocolVersion: '2025-11-25',
    capabilities: {},
    clientInfo: { name: 'measure-step2', version: '1.0' }
  }, 1);
  await rpc('notifications/initialized', {}, undefined);

  const tool = process.argv[2];
  const args = process.argv[3] ? JSON.parse(process.argv[3]) : {};

  const r = await rpc('tools/call', { name: tool, arguments: args }, 2);

  // El texto del tool es JSON dentro de JSON
  const txt = r?.result?.content?.[0]?.text;
  if (txt) {
    try { console.log(JSON.stringify(JSON.parse(txt), null, 2)); }
    catch { console.log(txt); }
  } else {
    console.log(JSON.stringify(r, null, 2));
  }
})().catch(e => { console.error('ERR', e.message); process.exit(1); });
