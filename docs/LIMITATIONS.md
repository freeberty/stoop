# Limitations and verification status

This was built unattended in a sandbox with **no access to NuGet, pub.dev, Docker registries or the Flutter SDK**,
and no Docker daemon. Anything depending on those is unverified. Be skeptical of it.

## Verified (actually run)

| Item | How |
|---|---|
| Application layer builds; 69 tests pass | Run through a reflection-based shim runner (xunit itself could not be restored). A mutation check confirmed the tests fail when rules are broken. |
| API + SignalR hub run and behave | `scripts/e2e-demo.mjs`: 59 checks over HTTP and real WebSockets, with Nostr signing done by an independent library (nostr-tools). Covers the section 18 demo flow. |
| PostgreSQL schema | `Sql/001_init.sql` applied to real PG16 + PostGIS 3.4: constraints, GiST index use on viewport queries. |
| Web client | `npm run build` (TypeScript strict) passes. Headless Chromium against the running API: map data loads, category filter, global list, search, no JS errors. |

## NOT verified

| Item | Risk |
|---|---|
| `Stoop.Infrastructure` (EF Core repositories, DbContext, migration wiring) | Never compiled. The query code (NTS envelope intersect, keyset paging with `Guid.CompareTo`, `ExecuteUpdateAsync`) may need fixes. Expect compile errors on first build. |
| NBitcoin Schnorr verifier | API signatures written from memory. The pure C# verifier is the tested fallback (`Nostr:Verifier=Managed`). |
| OpenAPI/Swagger packages, xunit package references | Versions unconfirmed. |
| Dockerfiles and `docker-compose.yml` | Never built or run. |
| Flutter app (`mobile/`) | Never analysed, built or run. Mapbox plugin API likely drifted. |
| EF vs in-memory behaviour | The in-memory store passes the tests; the EF implementations are not proven equivalent. |
| Trigram search index | Created, but not shown to be used by the planner. |
| Visual QA of web UI | One screenshot only; no mobile layout or dark-mode pass. |

## Known gaps

* Web login supports **NIP-07 only**. NIP-46 (remote signer) and NIP-55 (Android signer) are not implemented.
* No Nostr relay. Chat is server-managed by design; the relay is a future phase.
* **Paid** communities model the states (`AwaitingPayment`, `PaidUntil`, expiry) but have no payment processor.
* The migration is hand-written (runs `001_init.sql`) with no EF model snapshot; `PendingModelChangesWarning` is suppressed.
* No account merge when a Nostr key and an email already belong to different accounts; linking returns a conflict.
* Web token lives in `sessionStorage` (XSS-exposed); an httpOnly cookie is the production choice.
* Rate limiting is in-process (per-instance); chat uses a per-user sliding window. Multi-instance SignalR needs a backplane.
* The default map style is OpenFreeMap over the network; the sandbox could not load tiles, so basemap rendering is untested.
* Seed data is synthetic. No moderation tooling, notifications, image upload or password reset.

## First things to do on your machine

1. `dotnet build backend` and fix whatever the Infrastructure project reports.
2. `docker compose up --build`, then run `scripts/e2e-demo.mjs` against it.
3. `cd mobile && flutter create . && flutter analyze`.
