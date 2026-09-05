
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
