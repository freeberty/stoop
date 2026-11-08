# Intersect (POC)

Map-first community discovery. Communities may have a location, a context period ("what it is about"),
or both, or neither (global). Members of a community share a server-managed group chat.

> **Read `docs/LIMITATIONS.md` first.** It lists exactly what was verified and what was never compiled or run.

## Layout

| Path | What |
|---|---|
| `backend/src/Intersect.Application` | Domain, services, rules (`AccessPolicy`), in-memory store, Nostr verification. Only depends on the ASP.NET shared framework. |
| `backend/src/Intersect.Infrastructure` | EF Core + Npgsql + PostGIS persistence, NBitcoin-backed Schnorr verifier, SQL migration. **Not compiled in the authoring environment.** |
| `backend/src/Intersect.Api` | REST controllers (`/api/v1`), SignalR hub (`/hubs/chat`), auth handler, Swagger (`/docs`). |
| `backend/tests/Intersect.Tests` | xunit tests (69) over the application layer. |
| `web/` | Vite + TypeScript + MapLibre GL JS client (the working reference UI). |
| `mobile/` | Flutter + Mapbox spike. **Unvalidated.** |
| `scripts/e2e-demo.mjs` | End-to-end run of the demo scenario against a live API (59 checks). |

## Run with Docker Compose (intended path, not tested here)

```bash
cp .env.example .env        # set AUTH_JWT_SECRET (openssl rand -base64 48) and POSTGRES_PASSWORD
docker compose up --build
# web http://localhost:5173   api http://localhost:8080   docs http://localhost:8080/docs
```
The database is PostGIS 16. On first start the API migrates and seeds about 420 synthetic communities
(`SEED_ENABLED=true`). `AUTH_EXPECTED_DOMAIN` must equal the hostname the browser uses, because Nostr
login events are bound to it.

## Run without Docker or a database (this path was tested)

```bash
cd backend/src/Intersect.Api
ASPNETCORE_ENVIRONMENT=Development dotnet run -p:Offline=true      # in-memory store, seeded, http://localhost:5080
cd web && npm ci && npm run dev                                     # http://localhost:5173
```
`-p:Offline=true` builds without the Infrastructure project and OpenAPI package (no NuGet needed beyond the SDK).
With normal restore you can drop it and set `Persistence:Provider=InMemory` to get the same store.

## Tests

```bash
cd backend && dotnet test            # needs NuGet access for xunit
node scripts/e2e-demo.mjs            # API=http://127.0.0.1:5080 DOMAIN=localhost, after `npm ci` in scripts/
```

## Auth

* **Nostr:** `POST /auth/nostr/challenge` then sign a kind-22242 event (tags `challenge`, `domain`) and `POST /auth/nostr/verify`.
  Challenges are single-use, expire, and can be bound to a key. NIP-98 `Authorization: Nostr ...` is accepted on API calls.
  The server never sees or stores a private key. Signature checks sit behind `ISchnorrVerifier` (NBitcoin.Secp256k1 by
  default, a pure C# BIP-340 implementation as fallback/test oracle). The web client supports NIP-07 only.
* **Email + password** (PBKDF2-SHA256). Identities can be linked to one account.
* A project-operated relay is a later phase and is not built.

## Rules worth knowing

* Visibility (Public / Unlisted / Private), access (Open / Approval / Invitation / Verified) and pricing (Free / Paid) are independent.
* Private communities return 404 to outsiders. Pending, removed and banned members get 403 on chat. Removal revokes live chat access immediately.
* The time filter matches *context dates* only (never creation date). Undated communities appear only with `includeUndated=true`.
* Communities with no location never appear in map results; use `/discovery/global` ("Anywhere" in the web UI).
