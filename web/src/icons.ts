import {
  ArrowLeft, Bird, CalendarDays, Camera, Compass, CreditCard, Gamepad2, Globe, GraduationCap, Info, KeyRound, Landmark,
  Laptop, Lightbulb, LocateFixed, Mail, Map as MapIcon, MapPin, MessageCircle, Mic, Music, Palette, PartyPopper, Plane,
  Plus, ShieldCheck, SlidersHorizontal, Sparkles, Tent, Trophy, Users, UtensilsCrossed, X, createElement, type IconNode,
} from 'lucide';
import type { Category } from './api';

/** The icons the app uses (Lucide, ISC licence). Import only what is listed so the bundle stays small. */
export const I = {
  arrowLeft: ArrowLeft, calendar: CalendarDays, close: X, compass: Compass, creditCard: CreditCard, filters: SlidersHorizontal,
  globe: Globe, info: Info, key: KeyRound, locate: LocateFixed, mail: Mail, map: MapIcon, pin: MapPin, chat: MessageCircle,
  plus: Plus, shield: ShieldCheck, bird: Bird, users: Users,
} satisfies Record<string, IconNode>;

export const CATEGORY_ICON: Record<Category, IconNode> = {
  DigitalNomads: Laptop, Alumni: GraduationCap, Conference: Mic, Photography: Camera, Gaming: Gamepad2,
  Technology: Lightbulb, Arts: Palette, Music, Sports: Trophy, Outdoors: Tent, Food: UtensilsCrossed,
  Travel: Plane, History: Landmark, Social: PartyPopper, Other: Sparkles,
};

/** An inline SVG icon that inherits the text colour. Decorative by default (hidden from screen readers). */
export function icon(node: IconNode, opts: { size?: number; stroke?: number; class?: string } = {}): SVGElement {
  const svg = createElement(node);
  const size = String(opts.size ?? 18);
  svg.setAttribute('width', size);
  svg.setAttribute('height', size);
  svg.setAttribute('stroke-width', String(opts.stroke ?? 2));
  svg.setAttribute('aria-hidden', 'true');
  svg.setAttribute('focusable', 'false');
  svg.classList.add('ico');
  if (opts.class) svg.classList.add(...opts.class.split(' '));
  return svg;
}

/** Replaces <span data-icon="name"> placeholders in static HTML. */
export function hydrateIcons(root: ParentNode = document) {
  for (const slot of root.querySelectorAll<HTMLElement>('[data-icon]')) {
    const node = I[slot.dataset.icon as keyof typeof I];
    if (node) slot.replaceWith(icon(node, { size: Number(slot.dataset.size) || 18 }));
  }
}
