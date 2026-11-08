import { HttpTransportType, HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { API_URL, api, getToken, request, type Message } from './api';
import { clear, el, toast } from './dom';

const FRIENDLY: Record<string, string> = {
  chat_access_denied: 'You no longer have access to this chat.',
  conversation_closed: 'This chat is closed.',
  conversation_not_found: 'This conversation is not available.',
  message_rate_limited: 'You are sending messages too quickly. Wait a moment.',
  empty_message: 'Type a message first.',
  message_too_long: 'That message is too long.',
};

function friendly(e: unknown): string {
  const msg = (e as Error)?.message ?? '';
  for (const [code, text] of Object.entries(FRIENDLY)) if (msg.includes(code)) return text;
  return msg.replace(/^.*HubException:\s*/, '') || 'Something went wrong.';
}

/** Group chat for one community. Live delivery via SignalR (/hubs/chat); history and fallback sending via REST. */
export class ChatPanel {
  readonly root: HTMLElement;
  private list = el('div', { class: 'chat-list', role: 'log', 'aria-live': 'polite' });
  private status = el('div', { class: 'chat-status', hidden: true });
  private input = el('textarea', { class: 'chat-input', placeholder: 'Write a message…', rows: 1, maxlength: '4000', 'aria-label': 'Message' });
  private sendBtn = el('button', { class: 'btn primary', text: 'Send', type: 'submit' });
  private olderBtn = el('button', { class: 'btn ghost small', text: 'Load older messages', hidden: true });

  private conn: HubConnection | null = null;
  private conversationId: string | null = null;
  private meId = '';
  private canModerate = false;
  private nextBefore: string | null = null;
  private seen = new Set<string>();
  private revoked = false;

  constructor() {
    const form = el('form', { class: 'chat-form', onsubmit: (e) => { e.preventDefault(); void this.send(); } }, this.input, this.sendBtn);
    this.input.addEventListener('keydown', (e) => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); void this.send(); } });
    this.olderBtn.addEventListener('click', () => void this.loadOlder());
    this.root = el('div', { class: 'chat' }, this.status, this.olderBtn, this.list, form);
  }

  async open(conversationId: string, meId: string, canModerate: boolean) {
    await this.close();
    this.conversationId = conversationId; this.meId = meId; this.canModerate = canModerate;
    this.revoked = false; this.seen.clear(); this.nextBefore = null; clear(this.list);
    this.setEnabled(true); this.setStatus(null);

    try {
      const page = await api.messages(conversationId);
      page.items.forEach((m) => this.render(m, false));
      this.nextBefore = page.nextBefore; this.olderBtn.hidden = !this.nextBefore;
      this.scrollToEnd();
    } catch (e) { this.deny(friendly(e)); return; }

    const conn = new HubConnectionBuilder()
      .withUrl(`${API_URL}/hubs/chat`, { accessTokenFactory: () => getToken() ?? '', transport: HttpTransportType.WebSockets, skipNegotiation: true })
      .withAutomaticReconnect([0, 1000, 3000, 7000, 15000])
      .configureLogging(LogLevel.Warning).build();
    this.conn = conn;

    conn.on('ReceiveMessage', (m: Message) => { if (m.conversationId === this.conversationId) { this.render(m, true); this.scrollToEnd(); } });
    conn.on('MessageEdited', (m: Message) => { if (m.conversationId === this.conversationId) this.render(m, false); });
    conn.on('MessageDeleted', (e: { conversationId: string; messageId: string }) => {
      if (e.conversationId === this.conversationId) this.list.querySelector(`[data-id="${CSS.escape(e.messageId)}"]`)?.replaceWith(this.tombstone(e.messageId));
    });
    conn.on('AccessRevoked', (e: { conversationId: string; reason?: string }) => {
      if (e.conversationId !== this.conversationId) return;
      this.deny(e.reason === 'community_deleted' ? 'This community was deleted by its owner. The chat is closed.' : 'You were removed from this community. The chat is now closed for you.');
    });

    conn.onreconnecting(() => this.setStatus('Connection lost. Reconnecting…'));
    conn.onreconnected(async () => {
      try { await conn.invoke('JoinConversation', this.conversationId); this.setStatus(null); await this.resync(); }
      catch (e) { this.deny(friendly(e)); }
    });
    conn.onclose(() => { if (!this.revoked) this.setStatus('Offline. Messages will be sent over a slower fallback until you reload.'); });

    try { await conn.start(); await conn.invoke('JoinConversation', conversationId); }
    catch (e) { if (/chat_access_denied|conversation_not_found/.test((e as Error).message)) this.deny(friendly(e)); else this.setStatus('Live updates unavailable. You can still send and read messages.'); }
  }

  async close() {
    const c = this.conn; this.conn = null;
    if (c) { try { await c.stop(); } catch { /* ignore */ } }
  }

  private async resync() {
    if (!this.conversationId) return;
    const page = await api.messages(this.conversationId);
    page.items.forEach((m) => this.render(m, true));
    this.scrollToEnd();
  }

  private async loadOlder() {
    if (!this.conversationId || !this.nextBefore) return;
    const before = this.nextBefore;
    const page = await api.messages(this.conversationId, before);
    const anchor = this.list.firstChild;
    for (const m of [...page.items].reverse()) this.render(m, false, true);
    this.nextBefore = page.nextBefore; this.olderBtn.hidden = !this.nextBefore;
    (anchor as HTMLElement | null)?.scrollIntoView({ block: 'start' });
  }

  private async send() {
    const text = this.input.value.trim();
    if (!text || !this.conversationId || this.revoked) return;
    this.sendBtn.disabled = true;
    try {
      if (this.conn?.state === HubConnectionState.Connected) await this.conn.invoke('SendMessage', this.conversationId, text);
      else await request('POST', `/api/v1/conversations/${this.conversationId}/messages`, { content: text });
      this.input.value = '';
    } catch (e) { toast(friendly(e), 'error'); }
    finally { this.sendBtn.disabled = this.revoked; this.input.focus(); }
  }

  private deny(message: string) {
    this.revoked = true; this.setStatus(message, true); this.setEnabled(false);
  }

  private setEnabled(on: boolean) { this.input.disabled = !on; this.sendBtn.disabled = !on; }

  private setStatus(text: string | null, error = false) {
    this.status.hidden = !text; this.status.textContent = text ?? ''; this.status.classList.toggle('error', error);
  }

  private scrollToEnd() { this.list.scrollTop = this.list.scrollHeight; }

  private tombstone(id: string) { return el('div', { class: 'msg deleted', 'data-id': id }, el('em', { text: 'Message deleted' })); }

  /** All content is inserted as text. */
  private render(m: Message, append: boolean, prepend = false) {
    const existing = this.list.querySelector(`[data-id="${CSS.escape(m.id)}"]`);
    if (!existing && this.seen.has(m.id)) return;
    this.seen.add(m.id);
    const mine = m.senderUserId === this.meId;
    const node = m.deleted ? this.tombstone(m.id) : el('div', { class: `msg ${mine ? 'mine' : ''}`, 'data-id': m.id },
      el('div', { class: 'msg-meta' },
        el('strong', { text: m.senderDisplayName }),
        el('span', { text: new Date(m.createdAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) + (m.editedAt ? ' · edited' : '') }),
        (mine || this.canModerate) && el('button', { class: 'link-btn', text: 'Delete', 'aria-label': 'Delete message', onclick: async () => {
          try { await request('DELETE', `/api/v1/messages/${m.id}`); } catch (e) { toast(friendly(e), 'error'); }
        } })),
      el('div', { class: 'msg-body', text: m.content }));
    if (existing) existing.replaceWith(node);
    else if (prepend) this.list.prepend(node);
    else this.list.append(node);
    void append;
  }
}
