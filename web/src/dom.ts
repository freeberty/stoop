import type { Category, CommunitySummary } from './api';
import { I, icon } from './icons';

type Child = Node | string | null | undefined | false;
type Props = Partial<{ class: string; text: string; type: string; value: string; placeholder: string; title: string; disabled: boolean; hidden: boolean; checked: boolean; min: string; max: string; step: string; required: boolean; autocomplete: string; maxlength: string; onclick: (e: MouseEvent) => void; oninput: (e: Event) => void; onchange: (e: Event) => void; onkeydown: (e: KeyboardEvent) => void; onsubmit: (e: SubmitEvent) => void }> & Record<string, unknown>;

/** Builds DOM nodes. Text is always set via textContent/text nodes, never innerHTML, so user content cannot inject markup. */
export function el<K extends keyof HTMLElementTagNameMap>(tag: K, props: Props = {}, ...children: Child[]): HTMLElementTagNameMap[K] {
  const node = document.createElement(tag);
  for (const [k, v] of Object.entries(props)) {
    if (v === undefined || v === false) continue;
    if (k === 'class') node.className = String(v);
    else if (k === 'text') node.textContent = String(v);
    else if (k.startsWith('on') && typeof v === 'function') node.addEventListener(k.slice(2), v as EventListener);
    else if (k in node) (node as unknown as Record<string, unknown>)[k] = v;
    else node.setAttribute(k, String(v));
  }
  for (const c of children) if (c) node.append(c);
  return node;
}

export const $ = <T extends HTMLElement>(sel: string, root: ParentNode = document) => root.querySelector(sel) as T;

export function clear(node: Node) { while (node.firstChild) node.removeChild(node.firstChild); }

export const CATEGORY_LABELS: Record<Category, string> = {
  DigitalNomads: 'Digital nomads', Alumni: 'Alumni', Conference: 'Conferences', Photography: 'Photography', Gaming: 'Gaming',
  Technology: 'Technology', Arts: 'Arts', Music: 'Music', Sports: 'Sports', Outdoors: 'Outdoors', Food: 'Food',
  Travel: 'Travel', History: 'History', Social: 'Social', Other: 'Other',
};

export function formatContext(start: string | null, end: string | null): string {
  if (!start && !end) return 'Any time';
  const f = (s: string) => new Date(s + 'T00:00:00Z').toLocaleDateString(undefined, { month: 'short', year: 'numeric', timeZone: 'UTC' });
  const y = (s: string) => s.slice(0, 4);
  if (start && end) {
    if (start.endsWith('-01-01') && end.endsWith('-12-31')) return y(start) === y(end) ? y(start) : `${y(start)} – ${y(end)}`;
    return f(start) === f(end) ? f(start) : `${f(start)} – ${f(end)}`;
  }
  return start ? `Since ${f(start)}` : `Until ${f(end!)}`;
}

export function accessLabel(c: Pick<CommunitySummary, 'accessMode' | 'visibility' | 'pricingMode'>): string[] {
  const out: string[] = [];
  out.push(c.accessMode === 'Open' ? 'Open' : c.accessMode === 'ApprovalRequired' ? 'Approval required' : c.accessMode === 'InvitationOnly' ? 'Invitation only' : 'Verified only');
  if (c.visibility !== 'Public') out.push(c.visibility);
  if (c.pricingMode === 'Paid') out.push('Paid');
  return out;
}

export function debounce<T extends unknown[]>(fn: (...a: T) => void, ms: number) {
  let t: number | undefined;
  return (...a: T) => { clearTimeout(t); t = window.setTimeout(() => fn(...a), ms); };
}

export function toast(message: string, kind: 'info' | 'error' = 'info') {
  const host = $('#toasts');
  const node = el('div', { class: `toast ${kind}`, role: 'status', text: message });
  host.append(node);
  window.setTimeout(() => node.remove(), kind === 'error' ? 6000 : 3500);
}

export function modal(title: string, body: HTMLElement, onClose?: () => void): () => void {
  const close = () => { overlay.remove(); onClose?.(); };
  const overlay = el('div', { class: 'overlay', onclick: (e) => { if (e.target === overlay) close(); } },
    el('div', { class: 'modal', role: 'dialog', 'aria-label': title },
      el('div', { class: 'modal-head' }, el('h2', { text: title }), el('button', { class: 'icon-btn', 'aria-label': 'Close', onclick: close }, icon(I.close, { size: 20 }))),
      body));
  document.body.append(overlay);
  return close;
}
