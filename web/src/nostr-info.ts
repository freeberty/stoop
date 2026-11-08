import { el, modal } from './dom';
import { I, icon } from './icons';
import type { IconNode } from 'lucide';

type Platform = 'ios' | 'android' | 'desktop';

const link = (href: string, text: string) => el('a', { href, text, target: '_blank', rel: 'noopener noreferrer' });

const STEPS: Record<Platform, { label: string; note?: string; steps: (string | Node)[][] }> = {
  ios: {
    label: 'iPhone',
    note: 'Needs iOS 18 or newer and Safari.',
    steps: [
      ['Install ', link('https://apps.apple.com/app/nostash/id6744309333', 'Nostash'), ' from the App Store. It is free.'],
      ['Open Settings → Apps → Safari → Extensions, turn on Nostash and allow it for this site.'],
      ['Open Nostash and create a new key, or import one you already have.'],
      ['Save a backup of your private key (it starts with ', el('code', { text: 'nsec' }), ') in your password manager.'],
      ['Come back here in Safari, tap ', el('strong', { text: 'Continue with Nostr' }), ' and approve.'],
    ],
  },
  android: {
    label: 'Android',
    note: 'Chrome on Android cannot run extensions, so use Firefox.',
    steps: [
      ['Install ', link('https://play.google.com/store/apps/details?id=org.mozilla.firefox', 'Firefox'), ' from Google Play.'],
      ['In Firefox, open ⋮ → Extensions, find ', link('https://addons.mozilla.org/android/addon/nos2x-fox/', 'nos2x-fox'), ' and tap Add.'],
      ['Open the nos2x-fox settings and generate a new key, or paste one you already have.'],
      ['Save a backup of your private key (it starts with ', el('code', { text: 'nsec' }), ') in your password manager.'],
      ['Open this site in Firefox, tap ', el('strong', { text: 'Continue with Nostr' }), ' and approve.'],
    ],
  },
  desktop: {
    label: 'Computer',
    steps: [
      ['Install a signer extension such as ', link('https://getalby.com', 'Alby'), ' or ', link('https://github.com/fiatjaf/nos2x', 'nos2x'), '.'],
      ['Create a new key in the extension, or import one you already have.'],
      ['Save a backup of your private key (it starts with ', el('code', { text: 'nsec' }), ') in your password manager.'],
      ['Reload this page, click ', el('strong', { text: 'Continue with Nostr' }), ' and approve.'],
    ],
  },
};

function detectPlatform(): Platform {
  const ua = navigator.userAgent;
  if (/iPhone|iPad|iPod/.test(ua) || (ua.includes('Macintosh') && navigator.maxTouchPoints > 1)) return 'ios';
  if (/Android/.test(ua)) return 'android';
  return 'desktop';
}

/** Friendly explainer: what Nostr is and how to get a key on each platform. */
export function openNostrInfo() {
  const feature = (node: IconNode, title: string, text: string) =>
    el('div', { class: 'nx-feature' }, el('span', { class: 'nx-icon' }, icon(node, { size: 18 })),
      el('div', {}, el('strong', { text: title }), el('p', { text })));

  const stepsHost = el('div', { class: 'nx-steps' });
  const tabs = el('div', { class: 'nx-tabs', role: 'tablist' });
  const show = (p: Platform) => {
    for (const b of tabs.children) b.classList.toggle('active', (b as HTMLElement).dataset.p === p);
    const s = STEPS[p];
    stepsHost.replaceChildren(el('ol', {}, ...s.steps.map((parts) => el('li', {}, el('span', {}, ...parts)))));
    if (s.note) stepsHost.append(el('p', { class: 'muted small', text: s.note }));
  };
  for (const p of Object.keys(STEPS) as Platform[]) {
    const b = el('button', { class: 'nx-tab', type: 'button', role: 'tab', text: STEPS[p].label, onclick: () => show(p) });
    b.dataset.p = p;
    tabs.append(b);
  }
  show(detectPlatform());

  let close = () => {};
  close = modal('What is Nostr?', el('div', { class: 'nx' },
    el('div', { class: 'nx-hero' },
      el('div', { class: 'nx-badge' }, icon(I.key, { size: 26 })),
      el('p', { class: 'nx-lead', text: 'Nostr is a login you own. Instead of a username and password stored by a company, you have a key that lives in an app on your device.' })),
    el('div', { class: 'nx-features' },
      feature(I.globe, 'One key, every app', 'The same key signs you in to any Nostr app. No new account each time.'),
      feature(I.shield, 'Your key stays with you', 'Your signer app keeps the private key. Sites only receive a signature, never the key.'),
      feature(I.bird, 'Owned by no one', 'Nostr is an open protocol, like email. No company can lock you out or delete your identity.')),
    el('h3', { class: 'nx-h', text: 'Get your key' }),
    tabs,
    stepsHost,
    el('div', { class: 'nx-warn' },
      el('strong', { text: 'Your private key is your account.' }),
      el('span', { text: ' Nobody can reset it. Keep a backup somewhere safe and never paste it into a website.' })),
    el('div', { class: 'nx-foot' },
      link('https://nostr.how', 'Learn more at nostr.how'),
      el('button', { class: 'btn primary', type: 'button', text: 'Got it', onclick: () => close() }))));
}
