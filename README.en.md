# Lager

[![CI](https://github.com/The-Lady-Janna/LagerApp/actions/workflows/ci.yml/badge.svg)](https://github.com/The-Lady-Janna/LagerApp/actions/workflows/ci.yml)

A self-hosted warehouse management system (WMS) for small to medium warehouses: goods receipt, stock with lots and expiry dates, orders, picking with a wall-aware pick route, packing, shipping, returns, stock counts, CSV import/export, labels and reports. ASP.NET Core backend, React web UI (German and English) with a layout editor and a mobile picker with barcode scanning. A Docker quick start is included (Dockerfile, Compose, MySQL variant, optional Caddy for HTTPS); the container build has not been tested in practice yet (see the project status).

**Language note:** The primary language of this project is German. This page is an English summary of [README.md](README.md), which is the reference. **The user interface is available in German and English** (language switch in the sidebar and on the login page). **Code comments, server messages (API error texts) and all documentation under `docs/` are in German.**

**Stack:** ASP.NET Core 8 (Clean Architecture) · EF Core 8 · SQLite / MySQL · React 19 · Vite · Konva · TypeScript · TanStack Query · Zustand · react-i18next.

> **Project status:** Pre-1.0 (first release: see [CHANGELOG.md](CHANGELOG.md)). APIs and the database schema may still change (databases are upgraded automatically on startup). There is no multi-tenancy and lists are not paginated; the user interface is bilingual (German, English), while server messages and the documentation are German. Carrier integrations (DHL, UPS) and ERP/shop integrations are **not** implemented. The feature list below says what is complete, partial or API-only; open items are in [TODO.md](TODO.md).
>
> **Known limitation (Docker):** the Docker image has so far **never been built and started in practice** (only `docker compose config` and `dotnet publish` were checked). The container build should be run through once on a machine with a running Docker before the first release (login, deep link, PDF delivery note, data after `down` and `up`, MySQL variant, Caddy profile), see [TODO.md](TODO.md).

---

## Documentation

All documentation is written in German.

| File | Purpose |
|---|---|
| **[docs/GETTING_STARTED.md](docs/GETTING_STARTED.md)** | Installation, Docker, first start, demo mode, MySQL, production setup, backup |
| **[docs/USAGE.md](docs/USAGE.md)** | User manual: all workflows (warehouse structure, articles, goods receipt, lots/expiry, picking, packing, shipping, stock counts, CSV, labels, backup ...) and known limitations |
| **[docs/CONFIGURATION.md](docs/CONFIGURATION.md)** | Every configuration key and environment variable (including Docker, backup, demo) |
| **[docs/API.md](docs/API.md)** | API overview: authentication, role matrix, error format, endpoints |
| **[docs/DATA_MODEL.md](docs/DATA_MODEL.md)** | Data model with ER diagram, ledger, state machines |
| **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)** | Developer docs: layers, auth, error handling, schema evolution, route registry, building features |
| **[docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md)** | Troubleshooting: lost password, locked account, Docker, HTTPS, CORS, restore, CSV import ... |
| **[TODO.md](TODO.md)** | Roadmap: what is done, what is deliberately postponed |
| **[CHANGELOG.md](CHANGELOG.md)** | Change history |

There is a detailed reference per area under `docs/features/`: [warehouse structure](docs/features/lager-stammdaten.md), [articles and GTIN](docs/features/artikel-gtin.md), [lots and expiry dates](docs/features/chargen-mhd.md), [CSV import and export](docs/features/csv-import-export.md), [backup and restore](docs/features/backup-restore.md), [labels](docs/features/etiketten.md), [demo mode](docs/features/demo-modus.md), [multilingual UI](docs/features/i18n.md) and [Swagger and OpenAPI](docs/features/openapi.md). Sample files to import are in `docs/samples/`.

---

## Requirements

- **Docker** with Docker Compose v2 if you only want to run the application (nothing else needed).
- **.NET SDK 8.0.419** or newer from the 8.0 line (`global.json`) and **Node.js** `^20.19.0` or `>=22.13.0` with npm 10 or newer for development.
- SQLite is built in; MySQL 8 / MariaDB is optional.

---

## Quick start

### With Docker (quick start)

```bash
git clone https://github.com/The-Lady-Janna/LagerApp.git lager
cd lager
docker compose up --build
# → http://localhost:8080 (UI and API from the same container)

# One-time admin password (printed only on the very first start):
docker compose logs lager
```

User `admin` with the one-time password from the log; the app immediately asks for a new password. **There is no default password.**

- **Data** (SQLite database, JWT key, logs, backups) lives in the volume `lager-data` (`/data` in the container). `docker compose down` keeps it, `docker compose down -v` **deletes it**.
- **Backup:** in the UI under **System > Backup & Restore** (admin only): create a backup now, download it, list, restore. Schedule and retention are set with `Backup__Schedule` and `Backup__RetentionCount` (put them in the `.env`; `docker-compose.yml` passes both through). Backups live in the volume under `/data/backups`, are unencrypted and should additionally be copied to another medium.
- **Settings** go into an `.env` next to `docker-compose.yml` (template: `.env.example`, everything optional). Important for access from the LAN or under a domain: `AllowedHosts=lager.example.com`, otherwise the API answers 400.
- **MySQL instead of SQLite:** `docker compose -f docker-compose.yml -f docker-compose.mysql.yml up --build` (required in `.env`: `LAGER_DB_PASSWORD` and `LAGER_DB_ROOT_PASSWORD`).
- **HTTPS via Caddy:** `docker compose --profile https up --build` with `LAGER_DOMAIN` in `.env` (example: `deploy/Caddyfile`). Camera scanning on a phone and the service worker need HTTPS.
- The container reports itself healthy via `GET /health/ready` (database reachable).

Details, variants and troubleshooting: **[docs/GETTING_STARTED.md](docs/GETTING_STARTED.md#schnellstart-mit-docker)** (German).

### Demo mode: try it with realistic sample data

Demo mode fills an **empty** database with an invented but realistic operation: a warehouse with bins and walls, over 40 articles with prices, lots with expiry dates, suppliers, customers, about 60 days of history (orders in every status, pick lists, shipments, purchasing, returns, stock counts) and demo users per role. Stock alerts, stock value, heat map, ABC, dead stock, picker performance and lot traceability show data right away. It is **off by default**.

```powershell
# local: demo mode via environment variable (set it on the very first start, the database must be empty)
$env:LAGER_DEMO = "1"
dotnet run --project src/Lager.Api
```

The **credentials** (admin: one-time password of the first start; `manager`, `picker`, `packer`, `receiver`, `viewer` ...: random passwords) are printed **once to the console** or the container log (`docker compose logs lager`), never to the log file and never in the repository. Docker runs as `Production` and therefore needs `Demo__Enabled=true` **and** `Demo__AllowInProduction=true` (put both lines in the `.env`; `docker-compose.yml` passes them through, see [docs/GETTING_STARTED.md](docs/GETTING_STARTED.md#5-demo-modus-und-demo-daten-erkunden)). The data set is described in [docs/features/demo-modus.md](docs/features/demo-modus.md) (German). Demo data does not belong in a production system.

### Developing locally

```powershell
# Backend (terminal 1)
dotnet run --project src/Lager.Api
# → http://localhost:5099 (environment Development), Swagger at /swagger

# Frontend (terminal 2)
cd frontend/lager-ui
npm ci
npm run dev
# → http://localhost:5173
```

**First login:** user `admin`. **There is no default password:** on the very first start the backend generates a one-time password and prints it **once to the console** (terminal 1). Sign in with it; the app immediately asks for a new password (at least 10 characters). Alternatively, set `Auth__BootstrapAdminPassword` before starting. Lost it? See [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md#passwort-verloren-oder-kein-admin-mehr) (German).

In the `Development` environment (the default profile of `dotnet run`) the deprecated switch `Database:Seed` (`appsettings.Development.json`) only creates a small data set (20 articles, one warehouse, 8 orders, no history). The full demo data set comes with `LAGER_DEMO=1` (see above). Details and the path to production: **[docs/GETTING_STARTED.md](docs/GETTING_STARTED.md)**.

---

## Screenshots

_Screenshots will follow (location: `docs/screenshots/`, demo data only)._ How the owner creates them in demo mode is described in [docs/screenshots/README.md](docs/screenshots/README.md) (German).

---

## Features

Status per feature: **UI** = usable in the web interface, **API** = REST API only, **partial** = with limitations (see [docs/USAGE.md](docs/USAGE.md#bekannte-einschränkungen), German).

**Master data**

| Feature | Status |
|---|---|
| Articles with dimensions, weight, stackability, stock thresholds, purchase price, default supplier, GTIN/EAN (with check digit) | UI |
| Bundles/kits (resolved when picking), season windows (checked when creating an order), alternative SKUs (used by article search and scan resolution as a fallback; there is no replacement-article suggestion for missing stock) | UI (article editor) |
| Resolving a scanned code by GTIN, SKU or alternative SKU (`GET /api/articles/by-code/...`) | API (the mobile picker and global search do not use it yet) |
| Warehouse structure: create, edit and delete warehouses, zones, aisles, shelves and bins; bin type (standard, HotPick, reserve) and replenishment threshold | UI (manager, page **Lagerstruktur**) |
| Warehouse layout: shelves, bins, walls, pick points, heat map | UI |
| Suppliers; customers with multiple addresses | UI |
| Users with roles (Admin, Manager, Receiver, Picker, Packer, Viewer) | UI |
| CSV import (articles, stock, orders; with dry run and row errors) and CSV export (plus movements and audit) | UI (manager, page **Import & Export**) |

**Operations**

| Feature | Status |
|---|---|
| Goods receipt with line items, putaway suggestions, lot and expiry date (header and lines are created atomically); goods receipt from a purchase order | UI (purchase order: **Wareneingang anlegen** in purchasing, does not book stock yet) |
| Stock with lots/expiry (booking with lot and expiry date, sub-rows per lot, next expiry date), manual adjustment, stock alerts | UI |
| Orders: manual, external API with idempotency, priority, due date, customer/shipping address, cancellation | UI (external API: API) |
| Picking: FEFO, expired lots are not picked, route around walls (shelves are not obstacles) | UI |
| Cart pick list (bundling by cart volume and weight, no 3D packing) and waves | UI |
| Mobile picker with barcode scanning (camera only over HTTPS or localhost, `BarcodeDetector`) | UI |
| Packing with actual quantities and stock booking; packing suggestion (carton heuristic); PDF delivery note with a Code 128 barcode of the order number | UI |
| Shipping with manual tracking numbers; DHL/UPS not connected | UI |
| Returns with quality check (A-grade, B-grade, defective, destroy) | UI (order reference: API) |
| Stock counts (snapshot, counting, reconciliation); replenishment (HotPick from reserve, bin type adjustable in the warehouse structure) | UI |
| Purchasing: order suggestions, purchase orders | UI |
| Labels for bins, articles and orders: printing in the browser (Code 128, single label 50 × 30 mm or A4 sheet 3 × 8) and ZPL download for Zebra printers | UI (page **Etiketten**) |

**Reports and system**

| Feature | Status |
|---|---|
| KPI dashboard (orders, pick lists, picks per hour, average pick distance), ABC analysis, heat map, slotting suggestions, dead stock, picker performance | UI |
| Stock valuation by FIFO from the ledger (Manager) | UI |
| Expiring lots (warning list with a horizon of 7/30/90 days) and lot traceability (receipts, stock, movements, possibly affected orders) | UI (reports, page **Chargen-Trace**) |
| Stock trend of an article | API |
| StockMovement ledger (append-only, with cost snapshot); no stock reservation | internal, reports via UI/API |
| Audit trail for articles, stock, orders, pick lists, layout objects and users (not for all objects, not a "full" audit trail); a CSV import appears as one summary entry | UI (Manager) |
| Backup and restore of the SQLite database: list, download, schedule, retention, restore with confirmation (locked in production until `Backup__AllowRestore` is set) | UI (admin, **System > Backup & Restore**) |
| Demo mode with sample data, history and demo users | configuration (`Demo__Enabled` or `LAGER_DEMO=1`) |
| Docker operation from a single container (UI and API), health endpoints `/health/live` and `/health/ready` | Docker Compose (container build not tested in practice yet) |
| UI in German and English (DE/EN switch in the sidebar and on the login page, choice stored per browser); server messages and the documentation stay German | UI ([docs/features/i18n.md](docs/features/i18n.md), German) |
| Swagger UI (`/swagger`) and OpenAPI description with summary, minimum role and error responses per endpoint; in `Development` or with `Swagger:Enabled=true` | configuration ([docs/features/openapi.md](docs/features/openapi.md), German) |
| Dark mode, global search (Ctrl/Cmd + K), PWA manifest and service worker (production build over HTTPS only) | UI |

---

## Security

Lager is **closed by default**: every endpoint except login (and the health endpoints) requires a token and the matching role. A production deployment still needs preparation before it goes anywhere near the internet:

- **Environment:** `dotnet run` starts as `Development` (Swagger, reseed, unrestricted restore). For production set `ASPNETCORE_ENVIRONMENT=Production`; the Docker image already runs that way. Swagger stays off there; if you switch it on with `Swagger__Enabled=true`, the API description becomes readable without signing in (the endpoints stay protected).
- **JWT key:** set `Jwt__SigningKey` (at least 32 bytes) or `Jwt__KeyFile`; without a key the API refuses to start in production. The Docker image stores the key in the volume on the first start (`/data/jwt.key`). Never commit it.
- **Admin access:** no default password; use the one-time password from the console or the container log, or `Auth__BootstrapAdminPassword`, and change it immediately. If you run an older database that was created with the former public default password, change that password **now**.
- **Demo mode** is for trying things out only: demo data and demo users do not belong in a production system (in `Production` the start aborts without explicit permission).
- **HTTPS:** terminate TLS at a reverse proxy (Caddy profile in the Compose setup) and set `Security__ForwardedHeaders__Enabled=true`; without a proxy use `Security__RequireHttps=true`. Never send credentials over plain HTTP.
- **Hosts and CORS:** set `AllowedHosts` and `Cors__AllowedOrigins__0` to your own domain.
- **Roles:** create users with the smallest roles they need; accounts are locked for 15 minutes after 5 failed attempts and login is rate-limited.
- **Backups** are unencrypted and contain password hashes; store them safely and never commit them. Databases, logs and keys do not belong in the repository (`.gitignore`).

The full checklist: [docs/GETTING_STARTED.md](docs/GETTING_STARTED.md#7-production-setup-kurz) (German). Please report vulnerabilities privately: [SECURITY.md](SECURITY.md) (German with an English summary).

---

## Project structure

```
src/
├── Lager.Domain/          entities + value objects (framework-free)
├── Lager.Application/     services, abstractions, pick route / packing optimizers, CSV import/export
├── Lager.Infrastructure/  EF Core, repositories, auth, schema steps, backup, carrier adapters
├── Lager.Contracts/       DTOs for the API and the application layer
└── Lager.Api/             ASP.NET Core: controllers, security, error handling, seeder, health, labels

frontend/lager-ui/         React + Vite + TypeScript + Konva (feature packages under src/features/)
tests/Lager.Tests/         xUnit tests (in-memory API, domain, algorithms, docs)
docs/                      guides and reference (German; features/ per area, samples/ CSV samples, screenshots/)
deploy/                    example for Caddy as a reverse proxy
Dockerfile                 one container with UI and API
docker-compose.yml         Compose setup (extra file docker-compose.mysql.yml for MySQL)
.env.example               template of the settings for Docker Compose
```

Architecture details: **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)** (German).

---

## Contributing

Contributions are welcome. Please read [CONTRIBUTING.md](CONTRIBUTING.md), [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) and [SECURITY.md](SECURITY.md) first (all in German); record changes under `[Unreleased]` in [CHANGELOG.md](CHANGELOG.md). Before opening a pull request, these checks must pass (CI runs them too):

```powershell
dotnet build Lager.sln -p:LagerStrict=true
dotnet test tests/Lager.Tests

cd frontend/lager-ui
npm ci
npm run lint
npm run typecheck
npm test
npm run build
```

`-p:LagerStrict=true` turns warnings into errors (as in CI). `npx tsc --noEmit` checks **nothing** in the frontend (the root `tsconfig.json` only contains project references); type checking is `npm run typecheck` (`tsc -b`). New entities or columns need a step in `src/Lager.Infrastructure/Persistence/SchemaSteps/` (there are no EF migrations), see [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#schema-evolution). Code comments and server messages are written in German; UI texts need a German and an English entry under `frontend/lager-ui/src/locales` (see [docs/features/i18n.md](docs/features/i18n.md)); issues and pull requests may be in German or English.

---

## Third-party licenses

The PDF delivery notes use **QuestPDF** under the **Community License**. It is free for, among others, open-source projects, education and companies with less than 1 million USD annual revenue. **Anyone who does not meet these conditions (for example larger companies operating Lager themselves) needs a commercial QuestPDF license** and has to change the license assignment in `src/Lager.Api/Documents/ShippingLabelRenderer.cs`. The wording on [questpdf.com/license](https://www.questpdf.com/license/) is authoritative; this is not legal advice. Other dependencies (NuGet, npm) are under their own respective licenses.

---

## License

This project is released under the **MIT License**, see [LICENSE](LICENSE). The QuestPDF dependency has its own license (see [Third-party licenses](#third-party-licenses)).
