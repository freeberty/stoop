import './styles.css';
import {
  ApiError, api, loginWithNostr, setToken, getToken,
  type AccessMode, type Category, type CommunityDetails, type CommunitySummary, type Feature, type FeatureCollection, type Member, type User, type Visibility,
} from './api';
import { CATEGORY_ICONS, highlightCard, initSheet, isMobile, renderCard, type Snap } from './browse';
import { ChatPanel } from './chat';
import { activeCount, applyClient, defaultFilters, openFilters, serverParams, timeLabel, type Filters } from './filters';
import { openNostrInfo } from './nostr-info';
import { $, CATEGORY_LABELS, accessLabel, clear, debounce, el, formatContext, modal, toast } from './dom';
import { CATEGORY_COLORS, MapLibreAdapter, type LngLat, type MapAdapter, type Viewport } from './map';

// ------------------------------------------------------------------ state

const STYLE_URL = (import.meta.env.VITE_MAP_STYLE_URL ?? 'https://tiles.openfreemap.org/styles/liberty') as string;
const FONT = (import.meta.env.VITE_MAP_FONT ?? 'Noto Sans Regular') as string;
const PAGE = 40; // cards rendered per "show more"

const state = {
  user: null as User | null,
  f: defaultFilters() as Filters,
  selectedId: null as string | null,
};

const map: MapAdapter = new MapLibreAdapter($('#map'), STYLE_URL, FONT);
const chat = new ChatPanel();

// ------------------------------------------------------------------ discovery (debounced, abortable, cached)

const cache = new Map<string, { at: number; fc: FeatureCollection }>();
let inflight: AbortController | null = null;

function viewportKey(f: Filters, v: Viewport) {
  const q = serverParams(f);
  q.set('west', v.west.toFixed(3)); q.set('south', v.south.toFixed(3));
  q.set('east', v.east.toFixed(3)); q.set('north', v.north.toFixed(3)); q.set('zoom', String(v.zoom));
  return { q, key: q.toString() + (getToken() ? '|u' : '|a') };
}

/** Fetches (or reuses) the communities for a viewport and filter set. */
async function load(f: Filters, v: Viewport, force: boolean, signal?: AbortSignal): Promise<FeatureCollection> {
  const { q, key } = viewportKey(f, v);
  const hit = cache.get(key);
  if (!force && hit && Date.now() - hit.at < 10_000) return hit.fc;
  const fc = await api.viewport(q, signal);
  cache.set(key, { at: Date.now(), fc });
  if (cache.size > 40) cache.delete(cache.keys().next().value as string);
  return fc;
}

async function refresh(v: Viewport = map.getViewport(), force = false) {
  inflight?.abort();
  const controller = (inflight = new AbortController());
  browse.classList.add('loading');
  try {
    show(await load(state.f, v, force, controller.signal));
  } catch (e) {
    if ((e as Error).name === 'AbortError') return;
    showError(e instanceof ApiError ? e.message : 'Could not load communities.');
  } finally { if (inflight === controller) browse.classList.remove('loading'); }
}

const refreshSoon = debounce((v: Viewport) => void refresh(v), 250);
map.onViewportChange(refreshSoon);

// ------------------------------------------------------------------ results list (left panel / bottom sheet)

const browse = $('#browse');
const list = $('#browse-list');
const browseTitle = $('#browse-title');
const browseSub = $('#browse-sub');
let shown: Feature[] = [];
let rendered = 0;

const cardHandlers = {
  open: (id: string) => void openCommunity(id, true),
  hover: (id: string | null) => map.setHovered(id),
};

function show(fc: FeatureCollection) {
  shown = applyClient(fc.features, state.f);
  map.setData({ ...fc, features: shown, count: shown.length });
  const n = shown.length;
  browseTitle.textContent = n === 0 ? 'No communities here' : `${n}${fc.truncated ? '+' : ''} communit${n === 1 ? 'y' : 'ies'} in this area`;
  const bits = [state.f.category ? CATEGORY_LABELS[state.f.category] : null, state.f.timeOn ? `about ${timeLabel(state.f)}` : null].filter(Boolean);
  browseSub.textContent = bits.length ? bits.join(' · ') : 'Move the map to explore';
  clear(list); rendered = 0;
  if (n === 0) {
    list.append(el('div', { class: 'browse-empty' },
      el('p', { text: 'Try zooming out, moving the map, or removing some filters.' }),
      (state.f.category || activeCount(state.f)) ? el('button', { class: 'btn', type: 'button', text: 'Clear filters', onclick: () => setFilters(defaultFilters()) }) : null));
    return;
  }
  renderMore();
}

function renderMore() {
  list.querySelector('.browse-more')?.remove();
  const next = shown.slice(rendered, rendered + PAGE);
  for (const f of next) list.append(renderCard(f.properties, cardHandlers));
  rendered += next.length;
  if (rendered < shown.length)
    list.append(el('button', { class: 'btn browse-more', type: 'button', text: `Show ${Math.min(PAGE, shown.length - rendered)} more`, onclick: renderMore }));
}

function showError(message: string) {
  browseTitle.textContent = 'Could not load communities';
  browseSub.textContent = message;
  clear(list);
  list.append(el('div', { class: 'browse-empty' }, el('button', { class: 'btn', type: 'button', text: 'Try again', onclick: () => void refresh(map.getViewport(), true) })));
}

// Phones: the list is a bottom sheet over the map with a floating Map / List switch.
const viewBtn = $<HTMLButtonElement>('#btn-view');
const sheet = initSheet(browse, (s) => syncViewButton(s));
function syncViewButton(s: Snap = sheet.get()) {
  viewBtn.hidden = !isMobile() || s !== 'full'; // in list mode, a quick way back to the map
  viewBtn.textContent = '🗺️ Map';
  document.body.dataset.sheet = isMobile() ? s : '';
}
viewBtn.addEventListener('click', () => sheet.set('peek'));
window.matchMedia('(max-width: 899px)').addEventListener('change', () => syncViewButton());
syncViewButton();

// ------------------------------------------------------------------ filters

function setFilters(f: Filters) {
  state.f = f;
  renderCatbar();
  const n = activeCount(f);
  const badge = $('#filters-count');
  badge.hidden = n === 0; badge.textContent = String(n);
  $('#btn-filters').classList.toggle('active', n > 0);
  void refresh(map.getViewport(), true);
}

$('#btn-filters').addEventListener('click', () => openFilters(state.f, {
  count: async (draft) => applyClient((await load(draft, map.getViewport(), false)).features, draft).length,
  apply: (f) => setFilters(f),
}));

// Category bar: icon + label, like a tab strip.
const catbar = $('#catbar');
function renderCatbar() {
  clear(catbar);
  const item = (cat: Category | null, icon: string, label: string) => {
    const active = state.f.category === cat;
    return el('button', { class: `cat ${active ? 'active' : ''}`, type: 'button', 'aria-pressed': String(active),
      onclick: () => setFilters({ ...state.f, category: active && cat ? null : cat }) },
      el('span', { class: 'cat-icon', 'aria-hidden': 'true', text: icon }), el('span', { class: 'cat-label', text: label }));
  };
  catbar.append(item(null, '🧭', 'All'));
  for (const cat of Object.keys(CATEGORY_LABELS) as Category[]) catbar.append(item(cat, CATEGORY_ICONS[cat], CATEGORY_LABELS[cat]));
}
renderCatbar();

// ------------------------------------------------------------------ search bar

const searchInput = $<HTMLInputElement>('#search');
const results = $('#search-results');

const runSearch = debounce(async (text: string) => {
  clear(results);
  if (text.trim().length < 2) { results.hidden = true; return; }
  try {
    const { items } = await api.search(text.trim());
    results.hidden = false;
    if (items.length === 0) results.append(el('div', { class: 'result empty', text: 'No communities found' }));
    for (const c of items) {
      results.append(el('button', { class: 'result', onclick: () => { results.hidden = true; searchInput.value = c.name; void openCommunity(c.id, true); } },
        el('span', { class: 'dot', style: `background:${CATEGORY_COLORS[c.category]}` }),
        el('span', { class: 'result-text' }, el('strong', { text: c.name }), el('small', { text: c.locationName ?? 'Anywhere' }))));
    }
  } catch { results.hidden = true; }
}, 250);
searchInput.addEventListener('input', () => runSearch(searchInput.value));
searchInput.addEventListener('keydown', (e) => { if (e.key === 'Escape') { results.hidden = true; searchInput.blur(); } });
document.addEventListener('click', (e) => { if (!(e.target as HTMLElement).closest('.searchbox')) results.hidden = true; });

// ------------------------------------------------------------------ preview card + selection

const preview = $('#preview');

function categoryPill(cat: Category) {
  return el('span', { class: 'pill', style: `--c:${CATEGORY_COLORS[cat]}` }, el('span', { class: 'dot', style: `background:${CATEGORY_COLORS[cat]}` }), CATEGORY_LABELS[cat]);
}

function showPreview(f: Feature) {
  const p = f.properties;
  map.clearDraftPin();
  state.selectedId = p.id; map.setSelected(p.id);
  clear(preview); preview.hidden = false;
  preview.append(
    el('button', { class: 'icon-btn close', 'aria-label': 'Close preview', text: '✕', onclick: clearSelection }),
    categoryPill(p.category),
    el('h3', { text: p.name }),
    el('p', { class: 'muted', text: `📍 ${p.locationName ?? 'Location not set'}` }),
    el('p', { class: 'muted', text: `🕘 ${formatContext(p.contextStartDate, p.contextEndDate)} · 👥 ${p.memberCount}` }),
    el('div', { class: 'badges' }, ...accessLabel(p).map((t) => el('span', { class: 'badge', text: t }))),
    el('button', { class: 'btn primary wide', text: 'View community', onclick: () => void openCommunity(p.id, false) }));
}

function clearSelection() { state.selectedId = null; map.setSelected(null); map.clearDraftPin(); preview.hidden = true; }
map.onSelect((f) => { showPreview(f); highlightCard(list, f.properties.id); if (isMobile() && sheet.get() === 'full') sheet.set('half'); });
// Tapping empty map closes an open card; otherwise it drops a pin you can turn into a community.
map.onBackgroundClick((at) => {
  if (drawer.classList.contains('open')) return;
  if (!preview.hidden) { clearSelection(); return; }
  showPinCard(at);
});

function showPinCard(at: LngLat) {
  clearSelection();
  let pos = at;
  map.showDraftPin(pos, (p) => { pos = p; });
  clear(preview); preview.hidden = false;
  preview.append(
    el('button', { class: 'icon-btn close', 'aria-label': 'Remove pin', text: '✕', onclick: clearSelection }),
    el('div', { class: 'pin-card-head' }, el('span', { class: 'pin-card-icon', 'aria-hidden': 'true', text: '📍' }), el('h3', { text: 'Start a community here?' })),
    el('p', { class: 'muted small', text: 'Drag the pin to fine-tune the spot. You can set the dates it is about in the next step.' }),
    state.user
      ? el('button', { class: 'btn primary wide', text: '＋ Create community here', onclick: () => { preview.hidden = true; openCreate(pos); } })
      : el('button', { class: 'btn primary wide', text: 'Log in to create a community', onclick: openAuth }));
}

// ------------------------------------------------------------------ community drawer

const drawer = $('#drawer');
let current: CommunityDetails | null = null;
let activeTab: 'about' | 'chat' | 'manage' = 'about';

function hideDrawer() { drawer.classList.remove('open'); void chat.close(); current = null; }
const communityPath = (slug: string) => `/c/${encodeURIComponent(slug)}`;
const slugFromPath = () => { const m = /^\/c\/([^/]+)\/?$/.exec(location.pathname); return m ? decodeURIComponent(m[1]) : null; };
function closeDrawer() { hideDrawer(); if (slugFromPath()) history.pushState(null, '', '/'); }

// Browser Back/Forward moves between the map and a community page.
window.addEventListener('popstate', () => {
  const slug = slugFromPath();
  if (slug) void openCommunity(slug, false);
  else hideDrawer();
});

async function openCommunity(id: string, fly: boolean, tab: 'about' | 'chat' | 'manage' = 'about') {
  try {
    const d = await api.community(id);
    current = d; activeTab = tab;
    const c = d.community;
    state.selectedId = c.id; map.setSelected(c.id);
    if (fly && c.latitude !== null && c.longitude !== null) map.flyTo(c.longitude, c.latitude, 14);
    if (location.pathname !== communityPath(c.slug)) history.pushState(null, '', communityPath(c.slug));
    preview.hidden = true;
    await renderDrawer();
  } catch (e) {
    toast(e instanceof ApiError && e.status === 404 ? 'That community does not exist or is private.' : (e as Error).message, 'error');
  }
}

async function reloadCurrent() { if (current) await openCommunity(current.community.id, false, activeTab); void refresh(map.getViewport(), true); }

async function renderDrawer() {
  if (!current) return;
  const d = current, c = d.community, mine = d.myMembership;
  const isActive = mine?.status === 'Active';
  const isAdmin = isActive && (mine!.role === 'Admin' || mine!.role === 'Owner');
  if (activeTab === 'chat' && !isActive) activeTab = 'about';
  if (activeTab === 'manage' && !isAdmin) activeTab = 'about';

  clear(drawer); drawer.classList.add('open');
  const tabs = el('div', { class: 'tabs', role: 'tablist' });
  const tab = (key: typeof activeTab, label: string) => tabs.append(el('button', { class: `tab ${activeTab === key ? 'active' : ''}`, role: 'tab', 'aria-selected': String(activeTab === key), text: label, onclick: () => { activeTab = key; void renderDrawer(); } }));
  tab('about', 'About'); if (isActive) tab('chat', 'Chat'); if (isAdmin) tab('manage', 'Manage');

  drawer.append(
    el('div', { class: 'drawer-head' },
      el('div', {}, el('button', { class: 'back-btn', text: '← Back to map', onclick: closeDrawer }), el('div', {}, categoryPill(c.category)), el('h2', { text: c.name }))),
    tabs);

  const body = el('div', { class: 'drawer-body' });
  drawer.append(body);

  if (activeTab === 'chat' && d.conversationId && state.user) {
    body.classList.add('chat-body');
    body.append(chat.root);
    await chat.open(d.conversationId, state.user.id, isAdmin);
    return;
  }
  await chat.close();

  if (activeTab === 'manage') { await renderManage(body, c.id); return; }

  body.append(
    el('p', { class: 'muted', text: `📍 ${c.locationName ?? 'Anywhere in the world'}` }),
    el('p', { class: 'muted', text: `🕘 ${formatContext(c.contextStartDate, c.contextEndDate)}   ·   👥 ${c.memberCount} member${c.memberCount === 1 ? '' : 's'}` }),
    c.expiresAt ? el('p', { class: 'muted', text: `⏳ Active until ${new Date(c.expiresAt).toLocaleDateString()}` }) : '',
    el('div', { class: 'badges' }, ...accessLabel(c).map((t) => el('span', { class: 'badge', text: t }))),
    el('p', { class: 'desc', text: c.description || 'No description yet.' }),
    d.priceAmount ? el('p', { class: 'price', text: `${d.priceAmount} ${d.priceCurrency ?? ''} per period. Payments are not part of this proof of concept.` }) : '',
    el('div', { class: 'actions' }, ...actionsFor(d)));
}

function actionsFor(d: CommunityDetails): Node[] {
  const c = d.community, st = d.myMembership?.status;
  const run = (fn: () => Promise<unknown>, okMsg: string) => async () => {
    try { await fn(); toast(okMsg); await reloadCurrent(); }
    catch (e) { if (e instanceof ApiError && e.status === 401) openAuth(); else toast(e instanceof ApiError && e.code === 'payment_required' ? 'Payment is required. Payments are not part of this POC.' : (e as Error).message, 'error'); await reloadCurrent(); }
  };
  if (!state.user) return [el('button', { class: 'btn primary wide', text: 'Log in to join', onclick: openAuth })];
  switch (st) {
    case 'Active':
      return [el('button', { class: 'btn primary wide', text: '💬 Open chat', onclick: () => { activeTab = 'chat'; void renderDrawer(); } }),
        d.myMembership!.role !== 'Owner' && el('button', { class: 'btn ghost wide', text: 'Leave community', onclick: run(() => api.leave(c.id), 'You left the community') })].filter(Boolean) as Node[];
    case 'Pending': return [el('div', { class: 'note', text: '⏳ Your request is waiting for an administrator.' })];
    case 'AwaitingPayment': return [el('div', { class: 'note', text: '💳 Payment is required to unlock this community (not available in the POC).' })];
    case 'Banned': return [el('div', { class: 'note error', text: 'You cannot join this community.' })];
    case 'Invited': return [el('button', { class: 'btn primary wide', text: 'Accept invitation', onclick: run(() => api.join(c.id), 'Welcome!') })];
  }
  switch (c.accessMode) {
    case 'Open': case 'Verified':
      return [el('button', { class: 'btn primary wide', text: c.accessMode === 'Verified' ? 'Join (Nostr-verified members)' : 'Join', onclick: run(() => api.join(c.id), 'Joined!') })];
    case 'ApprovalRequired':
      return [el('button', { class: 'btn primary wide', text: 'Request access', onclick: run(() => api.requestAccess(c.id), 'Request sent') })];
    default: return [el('div', { class: 'note', text: '✉️ Invitation only.' })];
  }
}

async function renderManage(body: HTMLElement, communityId: string) {
  body.append(el('p', { class: 'muted', text: 'Loading members…' }));
  let members: Member[] = [];
  try { members = await api.members(communityId); } catch (e) { clear(body); body.append(el('p', { class: 'note error', text: (e as Error).message })); return; }
  clear(body);
  const act = (fn: () => Promise<unknown>, msg: string) => async () => { try { await fn(); toast(msg); await reloadCurrent(); } catch (e) { toast((e as Error).message, 'error'); } };
  const section = (title: string, rows: Member[], render: (m: Member) => Node[]) => {
    body.append(el('h4', { text: `${title} (${rows.length})` }));
    if (rows.length === 0) body.append(el('p', { class: 'muted', text: 'Nobody here.' }));
    for (const m of rows) body.append(el('div', { class: 'member-row' }, el('span', { text: `${m.displayName}${m.role !== 'Member' ? ` · ${m.role}` : ''}` }), el('span', { class: 'row-actions' }, ...render(m))));
  };
  section('Requests', members.filter((m) => m.status === 'Pending'), (m) => [
    el('button', { class: 'btn small primary', text: 'Approve', onclick: act(() => api.approve(communityId, m.userId), 'Approved') }),
    el('button', { class: 'btn small ghost', text: 'Decline', onclick: act(() => api.remove(communityId, m.userId, false), 'Declined') })]);
  section('Members', members.filter((m) => m.status === 'Active'), (m) => m.role === 'Owner' ? [] : [
    el('button', { class: 'btn small ghost', text: 'Remove', onclick: act(() => api.remove(communityId, m.userId, false), 'Member removed') }),
    el('button', { class: 'btn small danger', text: 'Ban', onclick: act(() => api.remove(communityId, m.userId, true), 'Member banned') })]);

  if (current?.myMembership?.role === 'Owner') {
    const name = current.community.name;
    body.append(el('div', { class: 'danger-zone' },
      el('h4', { text: 'Delete community' }),
      el('p', { class: 'muted small', text: 'Removes it from the map and search, and closes the chat for every member. This cannot be undone.' }),
      el('button', { class: 'btn danger', text: 'Delete community', onclick: async () => {
        if (!confirm(`Delete "${name}"? Members lose access to its chat. This cannot be undone.`)) return;
        try { await api.deleteCommunity(communityId); toast('Community deleted'); closeDrawer(); clearSelection(); cache.clear(); void refresh(map.getViewport(), true); }
        catch (e) { toast((e as Error).message, 'error'); }
      } })));
  }
}

// ------------------------------------------------------------------ global ("anywhere") communities

$('#btn-global').addEventListener('click', async () => {
  const list = el('div', { class: 'list' }, el('p', { class: 'muted', text: 'Loading…' }));
  modal('Communities without a location', el('div', {}, el('p', { class: 'muted', text: 'Interest-based communities that belong to everyone, not to a place on the map.' }), list));
  try {
    const q = serverParams(state.f); q.set('take', '50');
    const { items } = await api.global(q);
    clear(list);
    if (items.length === 0) list.append(el('p', { class: 'muted', text: 'Nothing matches the current filters.' }));
    for (const c of items) list.append(communityRow(c));
  } catch (e) { clear(list); list.append(el('p', { class: 'note error', text: (e as Error).message })); }
});

function communityRow(c: CommunitySummary) {
  return el('button', { class: 'result', onclick: () => { document.querySelector('.overlay')?.remove(); void openCommunity(c.id, false); } },
    el('span', { class: 'dot', style: `background:${CATEGORY_COLORS[c.category]}` }),
    el('span', { class: 'result-text' }, el('strong', { text: c.name }), el('small', { text: `${CATEGORY_LABELS[c.category]} · ${c.memberCount} members` })));
}

// ------------------------------------------------------------------ locate

const locateBtn = $<HTMLButtonElement>('#btn-locate');
locateBtn.addEventListener('click', async () => {
  locateBtn.disabled = true;
  try { await map.locateUser(); } catch (e) { toast((e as Error).message, 'error'); }
  locateBtn.disabled = false;
});

// ------------------------------------------------------------------ account

const accountSlot = $('#account');

function renderAccount() {
  clear(accountSlot);
  if (state.user) {
    accountSlot.append(
      el('button', { class: 'btn primary new-btn', title: 'Create a community', text: '＋ New', onclick: () => openCreate() }),
      el('button', { class: 'avatar', title: `${state.user.displayName} (click to log out)`, text: state.user.displayName.slice(0, 1).toUpperCase(), onclick: () => { if (confirm('Log out?')) logout(); } }));
  } else accountSlot.append(el('button', { class: 'btn primary', text: 'Log in', onclick: openAuth }));
}

function logout() { setToken(null); state.user = null; void chat.close(); cache.clear(); renderAccount(); void refresh(map.getViewport(), true); if (current) void openCommunity(current.community.id, false); }

async function onAuthenticated(token: string, user: User) {
  setToken(token); state.user = user; cache.clear(); renderAccount();
  toast(`Signed in as ${user.displayName}`);
  void refresh(map.getViewport(), true);
  if (current) await openCommunity(current.community.id, false);
}

function openAuth() {
  let close = () => {};
  const error = el('p', { class: 'note error', hidden: true });
  const fail = (e: unknown) => { error.hidden = false; error.textContent = e instanceof ApiError ? e.message : 'Something went wrong. Please try again.'; };

  const nostrBtn = el('button', { class: 'btn primary wide', text: '🔑 Continue with Nostr', onclick: async () => {
    error.hidden = true; nostrBtn.disabled = true;
    try { const r = await loginWithNostr(); close(); await onAuthenticated(r.token, r.user); } catch (e) { fail(e); } finally { nostrBtn.disabled = false; }
  } });

  const email = el('input', { type: 'email', placeholder: 'Email', autocomplete: 'email', required: true });
  const password = el('input', { type: 'password', placeholder: 'Password (min 10 characters)', autocomplete: 'current-password', required: true });
  const name = el('input', { type: 'text', placeholder: 'Display name (for new accounts)', autocomplete: 'nickname', maxlength: '40' });
  const submit = async (register: boolean) => {
    error.hidden = true;
    try {
      const r = register ? await api.register(email.value, password.value, name.value || email.value.split('@')[0]) : await api.login(email.value, password.value);
      close(); await onAuthenticated(r.token, r.user);
    } catch (e) { fail(e); }
  };

  close = modal('Welcome', el('div', { class: 'auth' },
    nostrBtn,
    el('p', { class: 'muted small', text: 'Your private key never leaves your signer. This site only receives a signature on a one-time challenge.' }),
    el('button', { class: 'nx-link', type: 'button', text: 'ⓘ What is Nostr? How do I get a key?', onclick: openNostrInfo }),
    el('div', { class: 'or', text: 'or use email' }),
    el('form', { onsubmit: (e) => { e.preventDefault(); void submit(false); } }, email, password, name,
      el('div', { class: 'row' }, el('button', { class: 'btn primary', type: 'submit', text: 'Log in' }), el('button', { class: 'btn ghost', type: 'button', text: 'Create account', onclick: () => void submit(true) }))),
    error));
}

// ------------------------------------------------------------------ create community

function openCreate(at?: LngLat) {
  let pos: LngLat = at ?? map.center();
  const field = (label: string, input: HTMLElement) => el('label', { class: 'field' }, el('span', { text: label }), input);
  const name = el('input', { type: 'text', required: true, maxlength: '80', placeholder: 'e.g. Lisbon Photo Walkers' });
  const desc = el('textarea', { rows: 3, maxlength: '2000', placeholder: 'What is this community about?' });
  const cat = el('select', {}, ...(Object.keys(CATEGORY_LABELS) as Category[]).map((k) => el('option', { value: k, text: CATEGORY_LABELS[k] })));
  const hasLocation = el('input', { type: 'checkbox', checked: true });
  const lat = el('input', { type: 'number', step: 'any', value: pos.lat.toFixed(5), min: '-90', max: '90' });
  const lon = el('input', { type: 'number', step: 'any', value: pos.lng.toFixed(5), min: '-180', max: '180' });
  const setPos = (p: LngLat) => { pos = p; lat.value = p.lat.toFixed(5); lon.value = p.lng.toFixed(5); map.showDraftPin(p, setPos); };
  const fromInputs = () => { const la = Number(lat.value), lo = Number(lon.value); if (Number.isFinite(la) && Number.isFinite(lo)) setPos({ lat: la, lng: lo }); };
  lat.addEventListener('change', fromInputs); lon.addEventListener('change', fromInputs);
  setPos(pos);
  const locName = el('input', { type: 'text', maxlength: '120', placeholder: 'Place name (optional)' });
  const start = el('input', { type: 'date' });
  const end = el('input', { type: 'date' });
  if (state.f.timeOn) { start.value = `${state.f.fromYear}-01-01`; end.value = `${state.f.toYear}-12-31`; } // match the time filter in use
  const expires = el('input', { type: 'date' });
  const vis = el('select', {}, el('option', { value: 'Public', text: 'Public: appears on the map' }), el('option', { value: 'Unlisted', text: 'Unlisted: only reachable by link' }), el('option', { value: 'Private', text: 'Private: members and invitees only' }));
  const access = el('select', {}, el('option', { value: 'Open', text: 'Open: anyone can join' }), el('option', { value: 'ApprovalRequired', text: 'Approval required' }), el('option', { value: 'InvitationOnly', text: 'Invitation only' }), el('option', { value: 'Verified', text: 'Verified (Nostr) members only' }));
  const pricing = el('select', {}, el('option', { value: 'Free', text: 'Free' }), el('option', { value: 'Paid', text: 'Paid (payments not in POC)' }));
  const price = el('input', { type: 'number', min: '1', step: '1', placeholder: 'Price (EUR)' });
  const error = el('p', { class: 'note error', hidden: true });
  const pickBtn = el('button', { class: 'btn ghost wide pick-btn', type: 'button', text: '📍 Pick the spot on the map', onclick: () => pickOnMap() });
  const locFields = el('div', { class: 'loc-fields' }, pickBtn, el('div', { class: 'grid2' }, field('Latitude', lat), field('Longitude', lon)));
  hasLocation.addEventListener('change', () => {
    locFields.hidden = !hasLocation.checked; locName.hidden = !hasLocation.checked;
    if (hasLocation.checked) setPos(pos); else map.clearDraftPin();
  });

  // Hide the form, let the user tap the map, then bring the form back with the new spot.
  const pickOnMap = () => {
    const overlay = form.closest('.overlay') as HTMLElement | null;
    if (overlay) overlay.hidden = true;
    const done = () => { map.setPickMode(null); banner.remove(); if (overlay) overlay.hidden = false; };
    const banner = el('div', { class: 'pick-banner', role: 'status' },
      el('span', { text: '📍 Tap the map where your community is' }),
      el('button', { class: 'btn small', type: 'button', text: 'Cancel', onclick: done }));
    document.body.append(banner);
    map.setPickMode((p) => { setPos(p); map.flyTo(p.lng, p.lat, Math.max(map.getViewport().zoom, 13)); done(); });
  };

  const form: HTMLFormElement = el('form', { class: 'create', onsubmit: async (e) => {
    e.preventDefault(); error.hidden = true;
    const dayEnd = expires.value ? new Date(expires.value + 'T23:59:59Z').toISOString() : null;
    try {
      const created = await api.create({
        name: name.value, description: desc.value, category: cat.value,
        locationName: hasLocation.checked ? locName.value || null : null,
        latitude: hasLocation.checked ? Number(lat.value) : null, longitude: hasLocation.checked ? Number(lon.value) : null,
        contextStartDate: start.value || null, contextEndDate: end.value || null, expiresAt: dayEnd,
        visibility: vis.value as Visibility, accessMode: access.value as AccessMode, pricingMode: pricing.value,
        priceAmount: pricing.value === 'Paid' ? Number(price.value) : null, priceCurrency: 'EUR',
      });
      close(); toast('Community created'); void refresh(map.getViewport(), true);
      void openCommunity(created.community.id, true);
    } catch (err) { error.hidden = false; error.textContent = (err as Error).message; }
  } },
    field('Name', name), field('Description', desc), field('Category', cat),
    el('label', { class: 'switch-row' }, hasLocation, el('span', { text: 'Place on the map (uncheck for a global community)' })),
    locFields, locName,
    el('p', { class: 'muted small', text: 'Dates describe the period the community is about, e.g. 1974 to 1975 for a graduating class. They are independent of when you create it.' }),
    el('div', { class: 'grid2' }, field('About period: from', start), field('About period: to', end)),
    field('Stops being active on (optional)', expires),
    field('Visibility', vis), field('Who can join', access), el('div', { class: 'grid2' }, field('Pricing', pricing), field('Price', price)),
    el('button', { class: 'btn primary wide', type: 'submit', text: 'Create community' }), error);
  const close = modal('Create a community', form, () => { map.setPickMode(null); map.clearDraftPin(); });
}

// ------------------------------------------------------------------ boot

async function boot() {
  if (getToken()) {
    try { state.user = await api.me(); } catch { setToken(null); }
  }
  renderAccount();
  const legacy = /^#c=(.+)$/.exec(location.hash); // old hash links
  if (legacy) history.replaceState(null, '', communityPath(decodeURIComponent(legacy[1])));
  const slug = slugFromPath();
  if (slug) void openCommunity(slug, true);
}
void boot();
