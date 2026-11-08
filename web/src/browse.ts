import type { Category, FeatureProps } from './api';
import { CATEGORY_LABELS, el, formatContext } from './dom';
import { CATEGORY_COLORS } from './map';

export const CATEGORY_ICONS: Record<Category, string> = {
  DigitalNomads: '💻', Alumni: '🎓', Conference: '🎤', Photography: '📷', Gaming: '🎮',
  Technology: '💡', Arts: '🎨', Music: '🎵', Sports: '⚽', Outdoors: '🏕️', Food: '🍜',
  Travel: '✈️', History: '🏛️', Social: '🥂', Other: '✨',
};

const ACCESS_TEXT: Record<FeatureProps['accessMode'], string> = {
  Open: 'Open to all', ApprovalRequired: 'Approval required', InvitationOnly: 'Invitation only', Verified: 'Verified members',
};

export interface CardHandlers { open(id: string): void; hover(id: string | null): void }

/** One community in the results list. All text goes in via textContent. */
export function renderCard(p: FeatureProps, h: CardHandlers): HTMLElement {
  const color = CATEGORY_COLORS[p.category];
  const card = el('article', { class: 'ccard', role: 'listitem', tabindex: '0', 'data-id': p.id, style: `--c:${color}` },
    el('div', { class: 'ccard-cover', 'aria-hidden': 'true' },
      el('span', { class: 'ccard-icon', text: CATEGORY_ICONS[p.category] }),
      el('span', { class: 'ccard-cat', text: CATEGORY_LABELS[p.category] }),
      p.pricingMode === 'Paid' ? el('span', { class: 'ccard-flag', text: 'Paid' }) : null),
    el('div', { class: 'ccard-body' },
      el('div', { class: 'ccard-top' },
        el('h3', { text: p.name }),
        el('span', { class: 'ccard-members', title: 'Members', text: `👥 ${p.memberCount}` })),
      el('p', { class: 'ccard-line', text: p.locationName ?? 'Location on map' }),
      el('p', { class: 'ccard-line', text: formatContext(p.contextStartDate, p.contextEndDate) }),
      p.description ? el('p', { class: 'ccard-desc', text: p.description }) : null,
      el('p', { class: 'ccard-access', text: ACCESS_TEXT[p.accessMode] })));
  card.addEventListener('click', () => h.open(p.id));
  card.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); h.open(p.id); } });
  card.addEventListener('mouseenter', () => h.hover(p.id));
  card.addEventListener('mouseleave', () => h.hover(null));
  return card;
}

/** Scrolls the card into view and flashes it (used when a pin is clicked). */
export function highlightCard(list: HTMLElement, id: string) {
  const card = list.querySelector<HTMLElement>(`[data-id="${CSS.escape(id)}"]`);
  if (!card) return;
  card.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
  card.classList.remove('flash'); void card.offsetWidth; card.classList.add('flash');
}

// ------------------------------------------------------------------ mobile bottom sheet

export type Snap = 'peek' | 'half' | 'full';
export const isMobile = () => window.matchMedia('(max-width: 899px)').matches;

/**
 * On phones the results panel is a bottom sheet with three resting points.
 * Drag the handle or header; a tap on the header toggles between peek and half.
 */
export function initSheet(sheet: HTMLElement, onChange: (s: Snap) => void) {
  const grab = [sheet.querySelector<HTMLElement>('.sheet-handle')!, sheet.querySelector<HTMLElement>('.browse-head')!];
  let startY = 0, startTop = 0, dragging = false, moved = false, lastY = 0, lastT = 0, velocity = 0;

  const offsets = () => {
    const h = sheet.getBoundingClientRect().height;
    return { full: 0, half: Math.round(h * 0.5), peek: h - 96 } as Record<Snap, number>;
  };
  const currentOffset = () => offsets()[sheet.dataset.snap as Snap] ?? 0;

  const set = (s: Snap) => { sheet.style.transform = ''; sheet.classList.remove('dragging'); sheet.dataset.snap = s; onChange(s); };

  const down = (e: PointerEvent) => {
    if (!isMobile() || (e.target as HTMLElement).closest('button')) return;
    dragging = true; moved = false;
    startY = lastY = e.clientY; lastT = performance.now(); velocity = 0;
    startTop = currentOffset();
    (e.currentTarget as HTMLElement).setPointerCapture(e.pointerId);
  };
  const move = (e: PointerEvent) => {
    if (!dragging) return;
    const dy = e.clientY - startY;
    if (Math.abs(dy) > 4) moved = true;
    if (!moved) return;
    const o = offsets();
    const y = Math.max(o.full, Math.min(o.peek, startTop + dy));
    const now = performance.now();
    velocity = (e.clientY - lastY) / Math.max(1, now - lastT);
    lastY = e.clientY; lastT = now;
    sheet.classList.add('dragging');
    sheet.style.transform = `translateY(${y}px)`;
  };
  const up = (e: PointerEvent) => {
    if (!dragging) return;
    dragging = false;
    const snap = sheet.dataset.snap as Snap;
    if (!moved) { set(snap === 'peek' ? 'half' : snap === 'half' ? 'peek' : 'half'); return; }
    const o = offsets();
    const y = Math.max(o.full, Math.min(o.peek, startTop + (e.clientY - startY)));
    if (velocity > 0.6) set(snap === 'full' ? 'half' : 'peek');        // flick down
    else if (velocity < -0.6) set(snap === 'peek' ? 'half' : 'full'); // flick up
    else set((Object.entries(o) as [Snap, number][]).sort((a, b) => Math.abs(a[1] - y) - Math.abs(b[1] - y))[0][0]);
  };
  for (const g of grab) {
    g.addEventListener('pointerdown', down);
    g.addEventListener('pointermove', move);
    g.addEventListener('pointerup', up);
    g.addEventListener('pointercancel', up);
  }
  return { set, get: () => sheet.dataset.snap as Snap };
}
