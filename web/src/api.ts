export const API_URL = (import.meta.env.VITE_API_URL ?? 'http://localhost:5080') as string;
/** Must equal the API's Auth:ExpectedDomain. Defaults to the hostname of the page. */
export const NOSTR_DOMAIN = (import.meta.env.VITE_NOSTR_DOMAIN ?? window.location.hostname) as string;

export type Category =
  | 'DigitalNomads' | 'Alumni' | 'Conference' | 'Photography' | 'Gaming' | 'Technology' | 'Arts'
  | 'Music' | 'Sports' | 'Outdoors' | 'Food' | 'Travel' | 'History' | 'Social' | 'Other';
export type Visibility = 'Public' | 'Private' | 'Unlisted';
export type AccessMode = 'Open' | 'ApprovalRequired' | 'InvitationOnly' | 'Verified';
export type PricingMode = 'Free' | 'Paid';
export type MemberStatus = 'Pending' | 'Invited' | 'AwaitingPayment' | 'Active' | 'Left' | 'Removed' | 'Banned';
export type MemberRole = 'Member' | 'Admin' | 'Owner';

export interface CommunitySummary {
  id: string; name: string; slug: string; description: string; category: Category; topic: string | null;
  coverImageUrl: string | null; locationName: string | null; latitude: number | null; longitude: number | null;
  contextStartDate: string | null; contextEndDate: string | null; expiresAt: string | null;
  visibility: Visibility; accessMode: AccessMode; pricingMode: PricingMode; status: string; memberCount: number;
}
export interface CommunityDetails {
  community: CommunitySummary; priceAmount: number | null; priceCurrency: string | null; createdAt: string;
  conversationId: string | null; myMembership: { role: MemberRole; status: MemberStatus; paidUntil: string | null } | null;
}
export interface FeatureProps {
  id: string; name: string; slug: string; category: Category; memberCount: number; locationName: string | null;
  contextStartDate: string | null; contextEndDate: string | null; accessMode: AccessMode; pricingMode: PricingMode; visibility: Visibility;
  /** Short excerpt; older API versions may omit it. */
  description?: string;
}
export interface Feature { type: 'Feature'; id: string; geometry: { type: 'Point'; coordinates: [number, number] }; properties: FeatureProps }
export interface FeatureCollection { type: 'FeatureCollection'; features: Feature[]; count: number; truncated: boolean }
export interface User { id: string; displayName: string; email: string | null; nostrPublicKey: string | null }
export interface AuthResponse { token: string; expiresAt: string; user: User }
export interface Message { id: string; conversationId: string; senderUserId: string; senderDisplayName: string; content: string; createdAt: string; editedAt: string | null; deleted: boolean }
export interface Member { userId: string; displayName: string; role: MemberRole; status: MemberStatus; joinedAt: string | null; requestedAt: string | null }

export class ApiError extends Error {
  constructor(public status: number, public code: string, message: string, public extra?: Record<string, unknown>) { super(message); }
}

// Session storage (cleared when the tab closes). Production hardening: move to an httpOnly cookie.
let token: string | null = sessionStorage.getItem('stoop.token');
export const getToken = () => token;
export function setToken(t: string | null) {
  token = t;
  if (t) sessionStorage.setItem('stoop.token', t); else sessionStorage.removeItem('stoop.token');
}

export async function request<T>(method: string, path: string, body?: unknown, signal?: AbortSignal): Promise<T> {
  const headers: Record<string, string> = {};
  if (body !== undefined) headers['content-type'] = 'application/json';
  if (token) headers.authorization = `Bearer ${token}`;
  let res: Response;
  try {
    res = await fetch(API_URL + path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), signal });
  } catch (e) {
    if ((e as Error).name === 'AbortError') throw e;
    throw new ApiError(0, 'network_error', 'Cannot reach the server. Check your connection.');
  }
  if (res.status === 204) return undefined as T;
  const text = await res.text();
  let json: any = null;
  try { json = text ? JSON.parse(text) : null; } catch { /* not JSON */ }
  if (!res.ok) throw new ApiError(res.status, json?.code ?? 'error', json?.detail ?? res.statusText, json ?? undefined);
  return json as T;
}

export const api = {
  viewport: (q: URLSearchParams, signal?: AbortSignal) => request<FeatureCollection>('GET', `/api/v1/discovery/communities?${q}`, undefined, signal),
  global: (q: URLSearchParams) => request<{ items: CommunitySummary[]; hasMore: boolean }>('GET', `/api/v1/discovery/global?${q}`),
  search: (text: string) => request<{ items: CommunitySummary[] }>('GET', `/api/v1/communities/search?q=${encodeURIComponent(text)}&take=8`),
  community: (id: string) => request<CommunityDetails>('GET', `/api/v1/communities/${id}`),
  create: (body: unknown) => request<CommunityDetails>('POST', '/api/v1/communities', body),
  deleteCommunity: (id: string) => request<void>('DELETE', `/api/v1/communities/${id}`),
  join: (id: string) => request<{ status: MemberStatus }>('POST', `/api/v1/communities/${id}/join`),
  leave: (id: string) => request<void>('POST', `/api/v1/communities/${id}/leave`),
  requestAccess: (id: string) => request<{ status: MemberStatus }>('POST', `/api/v1/communities/${id}/request-access`),
  approve: (id: string, userId: string) => request<unknown>('POST', `/api/v1/communities/${id}/approve/${userId}`),
  remove: (id: string, userId: string, ban: boolean) => request<void>('DELETE', `/api/v1/communities/${id}/members/${userId}?ban=${ban}`),
  members: (id: string, status?: MemberStatus) => request<Member[]>('GET', `/api/v1/communities/${id}/members${status ? `?status=${status}` : ''}`),
  me: () => request<User>('GET', '/api/v1/me'),
  login: (email: string, password: string) => request<AuthResponse>('POST', '/api/v1/auth/login', { email, password }),
  register: (email: string, password: string, displayName: string) => request<AuthResponse>('POST', '/api/v1/auth/register', { email, password, displayName }),
  messages: (conversationId: string, before?: string) => request<{ items: Message[]; nextBefore: string | null }>('GET', `/api/v1/conversations/${conversationId}/messages?limit=30${before ? `&before=${before}` : ''}`),
};

// ---------------------------------------------------------------- Nostr (NIP-07 browser signer)

interface Nip07 { getPublicKey(): Promise<string>; signEvent(event: object): Promise<Record<string, unknown>> }
declare global { interface Window { nostr?: Nip07 } }

/** The private key never reaches this app: the browser extension signs the challenge event. */
export async function loginWithNostr(): Promise<AuthResponse> {
  if (!window.nostr) throw new ApiError(0, 'no_signer', 'No Nostr signer found in this browser. Tap “What is Nostr?” below to set one up.');
  const publicKey = await window.nostr.getPublicKey();
  const challenge = await request<{ challenge: string; kind: number; domain: string }>('POST', '/api/v1/auth/nostr/challenge', { publicKey });
  const event = await window.nostr.signEvent({
    kind: challenge.kind,
    created_at: Math.floor(Date.now() / 1000),
    tags: [['challenge', challenge.challenge], ['domain', challenge.domain]],
    content: '',
  });
  return request<AuthResponse>('POST', '/api/v1/auth/nostr/verify', { event });
}
