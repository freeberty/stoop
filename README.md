# Stoop (POC)

Map-first community discovery. Communities may have a location, a context period ("what it is about"),
or both, or neither (global). Members of a community share a server-managed group chat.

The name comes from the front steps of city brownstones, where neighbours sit and talk.
(It was called Intersect while it was a proof of concept.)

**Live demo:** https://stoop.freeberty.com (API: https://stoop-api.freeberty.com, docs at `/docs`).
The demo runs the in-memory store, so all data resets whenever the API restarts.

> **Read `docs/LIMITATIONS.md` first.** It lists exactly what was verified and what was never compiled or run.

## Layout

| Path | What |
|---|---|
| `backend/src/Stoop.Application` | Domain, services, rules (`AccessPolicy`), in-memory store, Nostr verification. Only depends on the ASP.NET shared framework. |
| `backend/src/Stoop.Infrastructure` | EF Core + Npgsql + PostGIS persistence, NBitcoin-backed Schnorr verifier, SQL migration. **Not compiled in the authoring environment.** |
| `backend/src/Stoop.Api` | REST controllers (`/api/v1`), SignalR hub (`/hubs/chat`), auth handler, Swagger (`/docs`). |
| `backend/tests/Stoop.Tests` | xunit tests (69) over the application layer. |
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
cd backend/src/Stoop.Api
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

## Getting a Nostr key

Stoop signs you in with a Nostr key held by a signer app (NIP-07). The site only receives a signature on a
one-time challenge, never the key. The login screen has a **"What is Nostr?"** button with the same steps.

**iPhone / iPad** (iOS 18 or newer, Safari)
1. Install [Nostash](https://apps.apple.com/app/nostash/id6744309333) from the App Store (free).
2. Open Settings → Apps → Safari → Extensions, turn on Nostash and allow it for the site.
3. Open Nostash and create a new key, or import an existing one.
4. Back up the private key (starts with `nsec`) in a password manager.
5. Open https://stoop.freeberty.com in Safari, tap **Continue with Nostr** and approve.

**Android** (Firefox; Chrome on Android cannot run extensions)
1. Install [Firefox](https://play.google.com/store/apps/details?id=org.mozilla.firefox).
2. In Firefox, open ⋮ → Extensions and add [nos2x-fox](https://addons.mozilla.org/android/addon/nos2x-fox/).
3. Open the nos2x-fox settings and generate a new key, or paste an existing one.
4. Back up the private key (starts with `nsec`) in a password manager.
5. Open https://stoop.freeberty.com in Firefox, tap **Continue with Nostr** and approve.

**Computer:** install [Alby](https://getalby.com) or [nos2x](https://github.com/fiatjaf/nos2x), create or import a key, then log in.

Your private key is your account: nobody can reset it, so keep a backup and never paste it into a website.
Signer apps that use NIP-46 or NIP-55 (Amber, nsec.app) are not supported yet.

## Deployment

Pushing to `main` deploys automatically with GitHub Actions:

| Workflow | Runs when | Deploys to |
|---|---|---|
| `.github/workflows/deploy-api.yml` | `backend/**` changes | Fly app `stoop-api` (one machine, `fly deploy --ha=false`) |
| `.github/workflows/deploy-web.yml` | `web/**` changes | Cloudflare Pages project `stoop`, built with `VITE_API_URL=https://stoop-api.freeberty.com` |

Both can also be started by hand from the Actions tab. They need two repository secrets:

* `FLY_API_TOKEN`: a deploy token for the app (`fly tokens create deploy -a stoop-api`).
* `CLOUDFLARE_API_TOKEN`: an account token with **Cloudflare Pages: Edit** and no client IP filter
  (GitHub runners use changing addresses).

Hosting layout: `stoop.freeberty.com` is a Pages custom domain. `stoop-api.freeberty.com` is a proxied Cloudflare
record pointing at the Fly app's IPv6 address, so browsers never connect to `*.fly.dev` directly (some mobile networks
block it). API secrets on Fly: `Auth__JwtSecret`, `Auth__ExpectedDomain=stoop.freeberty.com`,
`Cors__Origins__0=https://stoop.freeberty.com`, `Cors__Origins__1=https://stoop-dzp.pages.dev`.

## Rules worth knowing

* Visibility (Public / Unlisted / Private), access (Open / Approval / Invitation / Verified) and pricing (Free / Paid) are independent.
* Private communities return 404 to outsiders. Pending, removed and banned members get 403 on chat. Removal revokes live chat access immediately.
* The time filter matches *context dates* only (never creation date). Undated communities appear only with `includeUndated=true`.
* Communities with no location never appear in map results; use `/discovery/global` ("Anywhere" in the web UI).
