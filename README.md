# Jewelry Manager: server

The backend of a business-management system I built for a real handmade-jewelry business: pricing,
orders, finances, invoices and an online-store (Shopify) connection. Hebrew, mobile-first, used daily.

The web client lives in a separate repository: **jewelry-manager-client**.

## What it does

| Area | Highlights |
|---|---|
| **Pricing** | A product's price is never stored. It is recomputed live from the current material prices, labor rate, fees and additions, so a change in settings is reflected everywhere. |
| **Orders** | Line items are frozen snapshots, so later changes never rewrite history. Numbering per business, preparation stages, discount by percent or final amount, soft delete. |
| **Finances** | Income and expenses, recurring expenses, net profit per period, CSV export. Income follows its order and is frozen once the order is completed. |
| **Invoices** | Private file storage behind an interface, a ZIP export per period for the accountant, and an invoice reader that fills the expense form from a photo (Gemini, OpenAI or Claude, swappable by configuration). |
| **Shopify** | Read-only by design: signed webhooks (HMAC), orders land in an approval area with automatic product matching, cancellations and edits only raise a warning, and nothing is ever written back to the store. |
| **Multi-tenancy** | Every business-scoped table carries `BusinessId`, and every query is filtered by the caller's business. |

## Stack

ASP.NET Core 10, EF Core (code-first migrations), PostgreSQL, Firebase Auth (Google sign-in, verified
server-side on every request), xUnit with integration tests against a real PostgreSQL database.
Deployed with Docker on Render (`render.yaml`).

## Architecture

```
Request -> FirebaseAuthMiddleware (identity + tenant)
        -> Controller   (HTTP only, DTO records)
        -> Service      (all business logic)
        -> AppDbContext (the only code that talks to the database)
```

- `Features/<Domain>/` holds each feature's controller, service and `Dtos/`. `Program.cs` only wires DI.
- Services never trust a client-supplied business id; the tenant comes from the verified user.
- Errors are domain exceptions mapped to HTTP codes in one filter; unexpected ones are logged with the request path.
- `JewelryManager.Tools` is a command-line project for one-off operations through the same services the API
  uses: product catalog to and from Excel, importing a store's order history, and settings export/import.

## Design decisions worth a look

- **Dynamic vs. frozen**: the catalog and prices are live; orders and income are snapshots.
- **Idempotent webhooks**: a store retry can never create an order twice (unique key plus an advisory lock).
- **A warning, never an automatic change**: when the store cancels or edits an order, money on the books is
  never touched. The owner decides.
- **Secrets only in the environment**: nothing sensitive is in the repository. Keys and connection strings
  are environment variables or git-ignored files.

## Run it locally

1. **Secrets (never committed).** Copy `.env.example` to `.env` and set real random passwords:
   ```
   DB_PASSWORD=...
   PGADMIN_PASSWORD=...
   ```
2. **Database.** `docker compose up -d` starts PostgreSQL (`localhost:5432`) and pgAdmin (`http://localhost:5050`).
3. **App config.** Create `JewelryManager.Api/appsettings.Development.json` (git-ignored):
   ```json
   {
     "ConnectionStrings": {
       "Default": "Host=localhost;Port=5432;Database=jewelry_manager_dotnet;Username=jewelry;Password=<DB_PASSWORD>"
     },
     "Seed": { "OwnerEmail": "<your Google email>" }
   }
   ```
4. **Firebase.** Put a service-account key at `JewelryManager.Api/firebase-adminsdk.json` (git-ignored).
5. **Run.** `dotnet run --project JewelryManager.Api` serves `http://localhost:3000`.
   In Development the app applies migrations and seeds default data on startup.

## Tests

`dotnet test`. The integration tests use a dedicated `jewelry_manager_test` database (password read from
`.env`, or set `TEST_DB_CONNECTION`), recreated on every run.

## Configuration

Everything that differs between environments is an environment variable (see `render.yaml`):
connection string, Firebase credentials, the invoice-reader provider and key, and the optional Shopify
connection (`Shopify__ShopDomain`, `Shopify__WebhookSecret`). `SHOPIFY.md` describes the store setup.
