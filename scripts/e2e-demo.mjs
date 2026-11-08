// End-to-end demo of the POC scenario (spec section 18) against a running API.
//   API=http://localhost:5080 DOMAIN=localhost node e2e-demo.mjs
// Nostr signing uses nostr-tools, a completely independent implementation from the server's.
import { generateSecretKey, getPublicKey, finalizeEvent } from 'nostr-tools/pure';
import * as signalR from '@microsoft/signalr';

const API = process.env.API ?? 'http://localhost:5080';
const DOMAIN = process.env.DOMAIN ?? 'localhost';
const LISBON = 'west=-9.5&south=38.5&east=-8.9&north=39.0';

let passed = 0;
const failures = [];
const log = (m) => console.log(m);
function check(name, cond, extra = '') {
  if (cond) { passed++; log(`  ok   ${name}`); }
  else { failures.push(name); log(`  FAIL ${name} ${extra}`); }
}

async function api(method, path, { token, body, headers = {} } = {}) {
  const res = await fetch(API + path, {
    method,
    headers: { ...(body ? { 'content-type': 'application/json' } : {}), ...(token ? { authorization: `Bearer ${token}` } : {}), ...headers },
    body: body ? JSON.stringify(body) : undefined,
  });
  let json = null;
  const text = await res.text();
  try { json = text ? JSON.parse(text) : null; } catch { /* not json */ }
  return { status: res.status, json };
}

async function nostrLogin(sk, { tamper } = {}) {
  const ch = (await api('POST', '/api/v1/auth/nostr/challenge', { body: {} })).json;
  const ev = finalizeEvent({
    kind: ch.kind, created_at: Math.floor(Date.now() / 1000),
    tags: [['challenge', ch.challenge], ['domain', DOMAIN]], content: '',
  }, sk);
  if (tamper) tamper(ev);
  return { ev, res: await api('POST', '/api/v1/auth/nostr/verify', { body: { event: ev } }) };
}

function nip98(sk, method, url) {
  const ev = finalizeEvent({ kind: 27235, created_at: Math.floor(Date.now() / 1000), tags: [['u', url], ['method', method]], content: '' }, sk);
  return 'Nostr ' + Buffer.from(JSON.stringify(ev)).toString('base64');
}

function connect(token) {
  return new signalR.HubConnectionBuilder()
    .withUrl(`${API}/hubs/chat`, { accessTokenFactory: () => token, skipNegotiation: true, transport: signalR.HttpTransportType.WebSockets })
    .configureLogging(signalR.LogLevel.None)
    .build();
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function waitFor(fn, ms = 3000) { const end = Date.now() + ms; while (Date.now() < end) { if (fn()) return true; await sleep(25); } return false; }

// ------------------------------------------------------------------------------------------
log('\n[0] health + seeded data');
check('GET /health is healthy', (await api('GET', '/health')).json?.status === 'healthy');
const seeded = (await api('GET', `/api/v1/discovery/communities?${LISBON}&zoom=11`)).json;
check('seeded Lisbon viewport returns many markers', seeded?.features?.length > 20, `got ${seeded?.features?.length}`);
check('features are GeoJSON points [lon,lat]', seeded.features[0].geometry.type === 'Point' && seeded.features[0].geometry.coordinates[0] < 0 && seeded.features[0].geometry.coordinates[1] > 30);
const seededGlobal = (await api('GET', '/api/v1/discovery/global?take=50')).json;
check('global communities listed separately, without coordinates', seededGlobal.items.length > 10 && seededGlobal.items.every((c) => c.latitude === null && c.longitude === null));
check('viewport never contains global communities', !seeded.features.some((f) => seededGlobal.items.some((g) => g.id === f.id)));

log('\n[1-3] users A and B authenticate with Nostr');
const skA = generateSecretKey();
const skB = generateSecretKey();
const a = await nostrLogin(skA);
const b = await nostrLogin(skB);
check('A authenticated', a.res.status === 200 && a.res.json.token);
check('B authenticated', b.res.status === 200 && b.res.json.token);
check('A account is bound to A public key', a.res.json.user.nostrPublicKey === getPublicKey(skA));
const tokenA = a.res.json.token, tokenB = b.res.json.token;
const idA = a.res.json.user.id, idB = b.res.json.user.id;

log('\n[auth hardening]');
check('replaying the same signed event is rejected', (await api('POST', '/api/v1/auth/nostr/verify', { body: { event: a.ev } })).json?.code === 'challenge_replayed');
const forged = await nostrLogin(generateSecretKey(), { tamper: (e) => { e.sig = e.sig.replace(/^../, e.sig.startsWith('00') ? '11' : '00'); } });
check('tampered signature rejected', forged.res.status === 401 && forged.res.json.code === 'bad_signature', JSON.stringify(forged.res.json));
const wrongDomain = await (async () => {
  const ch = (await api('POST', '/api/v1/auth/nostr/challenge', { body: {} })).json;
  const ev = finalizeEvent({ kind: ch.kind, created_at: Math.floor(Date.now() / 1000), tags: [['challenge', ch.challenge], ['domain', 'evil.example']], content: '' }, generateSecretKey());
  return api('POST', '/api/v1/auth/nostr/verify', { body: { event: ev } });
})();
check('wrong domain rejected', wrongDomain.json?.code === 'domain_mismatch');
check('unauthenticated create is 401', (await api('POST', '/api/v1/communities', { body: {} })).status === 401);
check('garbage bearer token is 401', (await api('GET', '/api/v1/me', { token: 'not.a.token' })).status === 401);
const me98 = await fetch(`${API}/api/v1/me`, { headers: { authorization: nip98(skA, 'GET', `http://${DOMAIN}/api/v1/me`) } });
check('NIP-98 signed request authenticates the registered Nostr user', me98.status === 200 && (await me98.json()).id === idA, `status ${me98.status}`);
const replay98 = nip98(skA, 'GET', `http://${DOMAIN}/api/v1/me`);
await fetch(`${API}/api/v1/me`, { headers: { authorization: replay98 } });
check('NIP-98 replay is rejected', (await fetch(`${API}/api/v1/me`, { headers: { authorization: replay98 } })).status === 401);

log('\n[4-5] A creates a public, approval-required community in Lisbon');
const created = await api('POST', '/api/v1/communities', {
  token: tokenA,
  body: { name: 'Lisbon Demo Circle', description: 'POC demo community', category: 'DigitalNomads', locationName: 'Alfama, Lisbon', latitude: 38.7110, longitude: -9.1280, visibility: 'Public', accessMode: 'ApprovalRequired', pricingMode: 'Free' },
});
check('community created (201)', created.status === 201, JSON.stringify(created.json));
const cid = created.json.community.id;
const convId = created.json.conversationId;
check('A is owner with a chat conversation', created.json.myMembership.role === 'Owner' && !!convId);

log('\n[6] B discovers it on the map');
const discovered = (await api('GET', `/api/v1/discovery/communities?${LISBON}&zoom=12&search=Demo%20Circle`, { token: tokenB })).json;
check('B finds it via viewport discovery', discovered.features.some((f) => f.id === cid));
check('B finds it via text search', (await api('GET', '/api/v1/communities/search?q=Demo%20Circle', { token: tokenB })).json.items.some((c) => c.id === cid));
const detailsB = (await api('GET', `/api/v1/communities/${cid}`, { token: tokenB })).json;
check('B sees details but no conversation id yet', detailsB.community.id === cid && !detailsB.conversationId);

log('\n[7-8] B requests access, A approves');
check('B cannot join directly (approval required)', (await api('POST', `/api/v1/communities/${cid}/join`, { token: tokenB })).json?.code === 'approval_required');
check('B request-access -> Pending', (await api('POST', `/api/v1/communities/${cid}/request-access`, { token: tokenB })).json?.status === 'Pending');
check('B cannot read chat while pending (403)', (await api('GET', `/api/v1/conversations/${convId}/messages`, { token: tokenB })).status === 403);
const outsiderTok = (await nostrLogin(generateSecretKey())).res.json.token;
check('a complete outsider gets 404 (conversation existence not revealed)', (await api('GET', `/api/v1/conversations/${convId}/messages`, { token: outsiderTok })).status === 404);
check('B cannot approve himself', (await api('POST', `/api/v1/communities/${cid}/approve/${idB}`, { token: tokenB })).status === 403);
check('B cannot list members', (await api('GET', `/api/v1/communities/${cid}/members`, { token: tokenB })).status === 403);
const pending = (await api('GET', `/api/v1/communities/${cid}/members?status=Pending`, { token: tokenA })).json;
check('A sees the pending request', pending.length === 1 && pending[0].userId === idB);
check('A approves B', (await api('POST', `/api/v1/communities/${cid}/approve/${idB}`, { token: tokenA })).json?.status === 'Active');

log('\n[9-11] both enter the group chat, A sends, B receives in real time');
const connA = connect(tokenA), connB = connect(tokenB);
const gotB = [], revokedB = [], gotA = [];
connA.on('ReceiveMessage', (m) => gotA.push(m));
connB.on('ReceiveMessage', (m) => gotB.push(m));
connB.on('AccessRevoked', (r) => revokedB.push(r));
await connA.start(); await connB.start();
await connA.invoke('JoinConversation', convId);
await connB.invoke('JoinConversation', convId);
check('both joined the conversation group', true);
const sent = await connA.invoke('SendMessage', convId, 'Hello from A <b>not html</b>');
check('A message acknowledged with server-assigned sender', sent.senderUserId === idA && sent.content.includes('Hello from A'));
check('B received it in real time', await waitFor(() => gotB.length === 1) && gotB[0].id === sent.id);
const history = (await api('GET', `/api/v1/conversations/${convId}/messages`, { token: tokenB })).json;
check('message persisted and visible in B history', history.items.some((m) => m.id === sent.id));
const restSent = (await api('POST', `/api/v1/conversations/${convId}/messages`, { token: tokenB, body: { content: 'REST reply from B', senderUserId: idA } })).json;
check('client-supplied sender id is ignored', restSent.senderUserId === idB);
check('REST-sent message is delivered live to A', await waitFor(() => gotA.some((m) => m.id === restSent.id)));
check('B sees conversation in /conversations', (await api('GET', '/api/v1/conversations', { token: tokenB })).json.some((c) => c.id === convId));

log('\n[12-14] A removes B: access revoked immediately, reconnect refused');
check('A removes B (204)', (await api('DELETE', `/api/v1/communities/${cid}/members/${idB}`, { token: tokenA })).status === 204);
check('B connection received AccessRevoked', await waitFor(() => revokedB.length === 1) && revokedB[0].conversationId === convId);
const gotBBefore = gotB.length;
await connA.invoke('SendMessage', convId, 'B should NOT see this');
await sleep(300);
check('B no longer receives live messages', gotB.length === gotBBefore);
let sendErr = null; try { await connB.invoke('SendMessage', convId, 'still here?'); } catch (e) { sendErr = e.message; }
check('B hub SendMessage rejected', sendErr?.includes('chat_access_denied'), sendErr);
let joinErr = null; try { await connB.invoke('JoinConversation', convId); } catch (e) { joinErr = e.message; }
check('B cannot rejoin the conversation (reconnect refused)', joinErr?.includes('chat_access_denied'), joinErr);
await connB.stop();
const connB2 = connect(tokenB); await connB2.start();
let rejoinErr = null; try { await connB2.invoke('JoinConversation', convId); } catch (e) { rejoinErr = e.message; }
check('fresh connection also refused', rejoinErr?.includes('chat_access_denied'), rejoinErr);
check('B REST history now forbidden', (await api('GET', `/api/v1/conversations/${convId}/messages`, { token: tokenB })).status === 403);
check('B REST send now forbidden', (await api('POST', `/api/v1/conversations/${convId}/messages`, { token: tokenB, body: { content: 'x' } })).status === 403);
await connB2.stop();

log('\n[15] community remains discoverable per its visibility');
check('still on the map', (await api('GET', `/api/v1/discovery/communities?${LISBON}&zoom=12&search=Demo%20Circle`)).json.features.some((f) => f.id === cid));
const afterDetails = (await api('GET', `/api/v1/communities/${cid}`, { token: tokenA })).json;
check('member count reflects removal', afterDetails.community.memberCount === 1);

log('\n[ban] banned users cannot come back');
const open = (await api('POST', '/api/v1/communities', { token: tokenA, body: { name: 'Open Demo Hall', category: 'Social', latitude: 38.72, longitude: -9.14, visibility: 'Public', accessMode: 'Open' } })).json;
check('B joins the open community', (await api('POST', `/api/v1/communities/${open.community.id}/join`, { token: tokenB })).json?.status === 'Active');
await api('DELETE', `/api/v1/communities/${open.community.id}/members/${idB}?ban=true`, { token: tokenA });
check('banned B cannot rejoin', (await api('POST', `/api/v1/communities/${open.community.id}/join`, { token: tokenB })).json?.code === 'banned');

log('\n[extras] global, historical, future, time filtering, private isolation');
const global = (await api('POST', '/api/v1/communities', { token: tokenA, body: { name: 'Demo Global Interest Club', category: 'Gaming', visibility: 'Public', accessMode: 'Open' } })).json;
check('global community created without location', global.community.latitude === null);
check('global community found via /discovery/global', (await api('GET', '/api/v1/discovery/global?search=Demo%20Global')).json.items.some((c) => c.id === global.community.id));
const hist = (await api('POST', '/api/v1/communities', { token: tokenA, body: { name: 'Demo Graduates 1974', category: 'Alumni', latitude: 38.7527, longitude: -9.1585, contextStartDate: '1974-01-01', contextEndDate: '1975-12-31', visibility: 'Public', accessMode: 'Open' } })).json;
const future = (await api('POST', '/api/v1/communities', { token: tokenA, body: { name: 'Demo Future Summit', category: 'Conference', latitude: 38.7685, longitude: -9.0945, contextStartDate: '2027-05-10', contextEndDate: '2027-05-12', visibility: 'Public', accessMode: 'Open' } })).json;
const byTime = async (from, to) => (await api('GET', `/api/v1/discovery/communities?${LISBON}&from=${from}&to=${to}&search=Demo`)).json.features.map((f) => f.properties.name);
const t1974 = await byTime('1970-01-01', '1980-12-31');
check('time filter 1970-1980 shows the 1974 community only', t1974.includes('Demo Graduates 1974') && !t1974.includes('Demo Future Summit'), JSON.stringify(t1974));
const t2027 = await byTime('2027-01-01', '2027-12-31');
check('time filter 2027 shows the future event only', t2027.includes('Demo Future Summit') && !t2027.includes('Demo Graduates 1974'), JSON.stringify(t2027));
const priv = (await api('POST', '/api/v1/communities', { token: tokenA, body: { name: 'Demo Secret Society', category: 'Social', latitude: 38.71, longitude: -9.13, visibility: 'Private', accessMode: 'InvitationOnly' } })).json;
check('private community absent from anonymous discovery', !(await api('GET', `/api/v1/discovery/communities?${LISBON}&search=Secret`)).json.features.some((f) => f.id === priv.community.id));
check('private community absent from search', !(await api('GET', '/api/v1/communities/search?q=Secret%20Society', { token: tokenB })).json.items.some((c) => c.id === priv.community.id));
check('private community is 404 for outsiders', (await api('GET', `/api/v1/communities/${priv.community.id}`, { token: tokenB })).status === 404);
check('owner still discovers their private community', (await api('GET', `/api/v1/discovery/communities?${LISBON}&search=Secret`, { token: tokenA })).json.features.some((f) => f.id === priv.community.id));
const bad = await api('GET', `/api/v1/discovery/communities?west=1&south=50&east=2&north=40`);
check('invalid bbox returns problem+json 400', bad.status === 400 && bad.json?.code === 'invalid_bbox');
const email = await api('POST', '/api/v1/auth/register', { body: { email: `demo-${Date.now()}@example.test`, password: 'correct horse battery', displayName: 'Email Demo' } });
check('email accounts work as an alternative', email.status === 200 && email.json.token);
const link = (await api('POST', '/api/v1/auth/nostr/link/challenge', { token: email.json.token, body: {} })).json;
const skC = generateSecretKey();
const linkEv = finalizeEvent({ kind: link.kind, created_at: Math.floor(Date.now() / 1000), tags: [['challenge', link.challenge], ['domain', DOMAIN]], content: '' }, skC);
const linked = await api('POST', '/api/v1/auth/nostr/link', { token: email.json.token, body: { event: linkEv } });
check('Nostr identity linked to email account', linked.status === 200 && linked.json.nostrPublicKey === getPublicKey(skC), JSON.stringify(linked.json));
check('logging in with that key now yields the email account', (await nostrLogin(skC)).res.json.user.id === email.json.user.id);

await connA.stop();
log(`\n${passed} checks passed, ${failures.length} failed`);
if (failures.length) { log('Failed: ' + failures.join('; ')); process.exit(1); }
