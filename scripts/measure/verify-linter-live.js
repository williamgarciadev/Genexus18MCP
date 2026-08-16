// Verificacion en vivo de GX012/GX014/GX015 + skill clean-architecture.
// Uso: node verify-linter-live.js   (contra el gateway scratch en 5001, KB MatikaPayment_2702)
const BASE = process.env.MCP_URL || 'http://127.0.0.1:5001/mcp';
let SESSION = null;

async function rpc(method, params, id) {
  const headers = { 'Content-Type': 'application/json', 'Accept': 'application/json, text/event-stream' };
  if (SESSION) headers['MCP-Session-Id'] = SESSION;
  const res = await fetch(BASE, { method: 'POST', headers, body: JSON.stringify({ jsonrpc: '2.0', id, method, params }) });
  const sid = res.headers.get('mcp-session-id'); if (sid) SESSION = sid;
  let text = await res.text();
  if (text.includes('data: ')) { const line = text.split('\n').find(l => l.startsWith('data: ')); text = line.slice(6); }
  return JSON.parse(text);
}

function toolText(r) { return JSON.parse(r.result.content[0].text); }

(async () => {
  await rpc('initialize', { protocolVersion: '2025-11-25', capabilities: {}, clientInfo: { name: 'verify', version: '1' } }, 1);
  await rpc('notifications/initialized', {}, undefined).catch(() => {});

  // 1) La skill se sirve completa por resources/read
  const skill = await rpc('resources/read', { uri: 'genexus://kb/skills/clean-architecture' }, 2);
  const body = skill.result?.contents?.[0]?.text || '';
  console.log('PRUEBA 1 - skill servida:', body.length, 'chars |',
    body.includes('no es un lenguaje orientado a objetos') && body.includes('GX015') ? 'OK' : 'FALLA');

  // 2) abrir KB
  const open = await rpc('tools/call', { name: 'genexus_kb', arguments: { action: 'open', path: 'D:/Proyectos/Matika/ModelosGX/MatikaPayment_2702' } }, 3);
  console.log('KB abierta:', toolText(open).opened || JSON.stringify(toolText(open)).slice(0, 100));

  // 3) GX015/GX012 contra un WebPanel WWP real (CompanyWW: 18 pares Start/End verificados)
  const lintWwp = toolText(await rpc('tools/call', { name: 'genexus_analyze', arguments: { mode: 'linter', name: 'CompanyWW' } }, 4));
  const issues = lintWwp.issues || lintWwp.result?.issues || [];
  const byCode = {};
  for (const i of issues) byCode[i.code] = (byCode[i.code] || 0) + 1;
  console.log('PRUEBA 2 - CompanyWW (WWP):', JSON.stringify(byCode));
  console.log('  GX012 suprimido (PatternInstance):', !byCode.GX012 ? 'OK' : 'FALLA (' + byCode.GX012 + ')');
  console.log('  GX015 sin falsos positivos en regiones generadas: reportados =', byCode.GX015 || 0, '(revisar lineas si >0)');
  for (const i of issues.filter(x => x.code === 'GX015')) console.log('    GX015 en linea', i.line, 'parte', i.part);
  process.exit(0);
})().catch(e => { console.error('ERROR:', e.message); process.exit(1); });
