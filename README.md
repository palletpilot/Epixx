
# Lagerkraft

This repository started as the Epixx proof of concept (below) and now hosts Lagerkraft: a lightweight, modular warehouse system built as multi-tenant SaaS with a database per tenant, an offline-first floor PWA, and a small set of .NET services on Postgres and NATS.

**Start here:** [docs/superpowers/specs/2026-09-05-lagerkraft-architecture-design.md](docs/superpowers/specs/2026-09-05-lagerkraft-architecture-design.md) holds the architecture and every design decision. Read "Decisions locked" and the scope decomposition first.

## Local development

Prerequisites: .NET 10 SDK 10.0.400, Docker, Node 24 (`corepack enable pnpm`).

```powershell
./scripts/dev.ps1
```

Or from Cursor: Terminal → Run Task → **Start Lagerkraft**. Either one starts AppHost (Postgres `platform` and `tenant_migrate`, NATS with JetStream, Mailpit, four services) plus the office app on http://localhost:5173 and the floor app on http://localhost:5174. The Aspire dashboard lists the services. Stop with Ctrl+C.

Without Aspire, `docker compose up -d` starts the same infrastructure; run each service with `dotnet run` against the fake connection strings in `appsettings.Development.json`. Do not run compose and AppHost at the same time.

Layout:

- `Epixx/` - the original POC. Frozen; it is the domain reference, not the product. Do not extend it. Run it with `dotnet run --project Epixx/Epixx.csproj` (needs .NET 10 SDK and SQL Server LocalDB), open https://localhost:7132, log in as `admin@test.com` / `Admin123!`.
- `docs/superpowers/specs/` - design specs; implementation plans per sub-project are added alongside.
- `backend/`, `frontend/`, `contracts/` - appear with sub-project 0 (foundation); see the repository layout section of the spec.

Branches: `master` is the POC as it was; Lagerkraft work happens on `lagerkraft/*` branches, starting with `lagerkraft/sp0-foundation`.

---

# 📦 Pallet Warehouse Management System (Epixx)
Jag ville bygga en applikation som simulerar att lager likt det jag arbetar på just nu. 

## 🚀 Highlights

* 🧠 **Affärslogik i fokus** – tydlig hantering av pallstatus och lagerflöden
* ⚡ **Concurrency-hantering** – hanterar race conditions mellan användare och bakgrundsjobb
* 🔄 **Background services** – automatiserar lagerprocesser (t.ex. pallet transfers)
* 🗄️ **Databasdesign** – relationsmodell för pallar, platser och reservationer
* 🧩 **Utbyggbar arkitektur** – byggd för att enkelt kunna vidareutvecklas

---

## 🛠️ Tech Stack

* **C# / .NET**
* **Entity Framework Core**
* **SQL Server**

---

## 📊 Core Concepts

### Pallet States

* `AwaitingStorage` → Ny pall registreras redo för att bli placerad i lagret
* `Stored` → Pall placerad i lager
* `PalletTransfer` → Pall under förflyttning
* `PackingAreaTransfer` → Pall som ska till packyta

### Warehouse Logic

* Pallplatser kan reserveras temporärt
* Endast en pall per plats
* Bakgrundsjobb kan påverka status och flöde

---

## ⚙️ Example Flow

```text
1. Pall skapas (Incoming)
2. Pallplats reserveras
3. Pall placeras → Stored
4. Background service triggar flytt → PalletTransfer
```

---

## ⚠️ Challenges & Learnings

Det här projektet fokuserar mycket på verkliga problem inom systemdesign:

* Hantering av **race conditions**
* Synkronisering mellan **API och background services**
* Vikten av **kontrollerade state transitions**
* Debugging av “osynliga” processer som påverkar data

---

## 🎯 Purpose

Projektet är byggt som en del av min utveckling inom backend och systemdesign, med fokus på:

* Realtidsliknande dataproblem
* Skalbar och tydlig affärslogik
* Praktisk erfarenhet av concurrency i .NET

---

## 📌 Future Improvements

* Implementera **RowVersion / optimistic concurrency**
* Introducera en tydlig **state machine** för statusar
* API-lager (REST) för extern integration
* Enkel frontend för visualisering av lagerstatus
