import type { AccessMode, Category, Feature } from './api';
import { debounce, el, modal } from './dom';

export const YEAR_MIN = 1900;
export const YEAR_MAX = 2040;

export type Price = 'any' | 'free' | 'paid';
export type Sort = 'members' | 'name' | 'period';

export interface Filters {
  category: Category | null;
  timeOn: boolean;
  fromYear: number;
  toYear: number;
  includeUndated: boolean;
  access: AccessMode[];
  price: Price;
  sort: Sort;
}

export const defaultFilters = (): Filters => ({
  category: null, timeOn: false, fromYear: 1970, toYear: 1979, includeUndated: false, access: [], price: 'any', sort: 'members',
});

/** Filters the API applies (category and period). */
export function serverParams(f: Filters): URLSearchParams {
  const q = new URLSearchParams();
  if (f.category) q.set('category', f.category);
  if (f.timeOn) {
    q.set('from', `${f.fromYear}-01-01`);
    q.set('to', `${f.toYear}-12-31`);
    if (f.includeUndated) q.set('includeUndated', 'true');
  }
  return q;
}

/** Filters the API does not support yet run on the loaded results (at most a few thousand). */
export function applyClient(features: Feature[], f: Filters): Feature[] {
  const out = features.filter((x) => {
    const p = x.properties;
    if (f.access.length && !f.access.includes(p.accessMode)) return false;
    if (f.price === 'free' && p.pricingMode !== 'Free') return false;
    if (f.price === 'paid' && p.pricingMode !== 'Paid') return false;
    return true;
  });
  const start = (x: Feature) => x.properties.contextStartDate ?? x.properties.contextEndDate ?? '';
  if (f.sort === 'name') out.sort((a, b) => a.properties.name.localeCompare(b.properties.name));
  else if (f.sort === 'period') out.sort((a, b) => (start(b) || '0000').localeCompare(start(a) || '0000'));
  else out.sort((a, b) => b.properties.memberCount - a.properties.memberCount);
  return out;
}

/** How many filters in the panel are set (the category bar is counted separately). */
export const activeCount = (f: Filters) => (f.timeOn ? 1 : 0) + (f.access.length ? 1 : 0) + (f.price !== 'any' ? 1 : 0);

export const timeLabel = (f: Filters) => (f.fromYear === f.toYear ? String(f.fromYear) : `${f.fromYear}–${f.toYear}`);

const ACCESS: [AccessMode, string][] = [
  ['Open', 'Open to all'], ['ApprovalRequired', 'Approval required'], ['InvitationOnly', 'Invitation only'], ['Verified', 'Verified (Nostr)'],
];

/**
 * Airbnb-style filter sheet. Edits a draft; the footer button shows how many communities the
 * draft would show in the current map area and applies it.
 */
export function openFilters(current: Filters, opts: { count(draft: Filters): Promise<number>; apply(f: Filters): void }) {
  const draft: Filters = { ...current, access: [...current.access] };
  const body = el('div', { class: 'fx' });
  const showBtn = el('button', { class: 'btn primary', type: 'button', text: 'Show communities' });
  let close = () => {};

  const recount = debounce(async () => {
    showBtn.textContent = 'Counting…';
    try {
      const n = await opts.count(draft);
      showBtn.textContent = n === 0 ? 'No communities here' : `Show ${n} communit${n === 1 ? 'y' : 'ies'}`;
    } catch { showBtn.textContent = 'Show communities'; }
  }, 250);

  const seg = <T extends string>(options: [T, string][], get: () => T, set: (v: T) => void) => {
    const wrap = el('div', { class: 'fx-seg', role: 'radiogroup' });
    const paint = () => { for (const b of wrap.children) b.classList.toggle('active', (b as HTMLElement).dataset.v === get()); };
    for (const [v, label] of options) {
      const b = el('button', { type: 'button', role: 'radio', text: label, onclick: () => { set(v); paint(); changed(); } });
      b.dataset.v = v; wrap.append(b);
    }
    paint();
    return wrap;
  };

  const section = (title: string, hint: string | null, ...content: Node[]) =>
    el('section', { class: 'fx-section' }, el('h3', { text: title }), hint ? el('p', { class: 'muted small', text: hint }) : null, ...content);

  function render() {
    body.replaceChildren(
      section('Sort by', null, seg<Filters['sort']>([['members', 'Most members'], ['name', 'Name'], ['period', 'Most recent period']], () => draft.sort, (v) => (draft.sort = v))),
      timeSection(),
      section('Who can join', 'Pick any that suit you. None picked means all.', accessChips()),
      section('Price', null, seg<Price>([['any', 'Any'], ['free', 'Free'], ['paid', 'Paid']], () => draft.price, (v) => (draft.price = v))),
    );
  }

  function timeSection() {
    const out = el('output', { class: 'fx-years', text: timeLabel(draft) });
    const range = (get: () => number, set: (v: number) => void, label: string) => el('label', { class: 'fx-range' },
      el('span', { text: label }),
      el('input', { type: 'range', min: String(YEAR_MIN), max: String(YEAR_MAX), step: '1', value: String(get()),
        oninput: (e) => {
          set(Number((e.target as HTMLInputElement).value));
          if (draft.fromYear > draft.toYear) { draft.fromYear = draft.toYear = get(); render(); }
          out.textContent = timeLabel(draft); changed();
        } }));
    const now = new Date().getFullYear();
    const presets: [string, number, number][] = [['1970s', 1970, 1979], ['1980s', 1980, 1989], ['1990s', 1990, 1999], ['2000s', 2000, 2009], ['This year', now, now], ['Future', now + 1, YEAR_MAX]];
    const details = el('div', { class: 'fx-time' },
      out,
      range(() => draft.fromYear, (v) => (draft.fromYear = v), 'From'),
      range(() => draft.toYear, (v) => (draft.toYear = v), 'To'),
      el('div', { class: 'fx-chips' }, ...presets.map(([t, a, b]) => el('button', {
        type: 'button', class: `fx-chip ${draft.fromYear === a && draft.toYear === b ? 'active' : ''}`, text: t,
        onclick: () => { draft.fromYear = a; draft.toYear = b; render(); changed(); } }))),
      el('label', { class: 'switch-row' },
        el('input', { type: 'checkbox', checked: draft.includeUndated, onchange: (e) => { draft.includeUndated = (e.target as HTMLInputElement).checked; changed(); } }),
        el('span', { text: 'Also show communities without dates' })));
    details.hidden = !draft.timeOn;
    return section('What period it is about', 'Matches the dates a community is about, like a graduating class of 1975, not when it was created.',
      seg<'any' | 'period'>([['any', 'Any time'], ['period', 'Pick years']], () => (draft.timeOn ? 'period' : 'any'),
        (v) => { draft.timeOn = v === 'period'; details.hidden = !draft.timeOn; }),
      details);
  }

  function accessChips() {
    return el('div', { class: 'fx-chips' }, ...ACCESS.map(([v, label]) => {
      const b = el('button', { type: 'button', class: `fx-chip ${draft.access.includes(v) ? 'active' : ''}`, text: label, 'aria-pressed': String(draft.access.includes(v)),
        onclick: () => {
          draft.access = draft.access.includes(v) ? draft.access.filter((a) => a !== v) : [...draft.access, v];
          b.classList.toggle('active', draft.access.includes(v)); b.setAttribute('aria-pressed', String(draft.access.includes(v))); changed();
        } });
      return b;
    }));
  }

  function changed() { recount(); }

  const clearBtn = el('button', { class: 'link-btn fx-clear', type: 'button', text: 'Clear all', onclick: () => {
    Object.assign(draft, { ...defaultFilters(), category: draft.category });
    render(); changed();
  } });
  showBtn.addEventListener('click', () => { opts.apply(draft); close(); });

  render();
  close = modal('Filters', el('div', { class: 'fx-wrap' }, body, el('div', { class: 'fx-foot' }, clearBtn, showBtn)));
  document.querySelector('.overlay:last-of-type .modal')?.classList.add('modal-filters');
  changed();
}
