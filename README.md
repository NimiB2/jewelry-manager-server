# jewelry-manager — server

ASP.NET Core 9 + EF Core + PostgreSQL backend for the jewelry business manager.
Firebase Auth (Google sign-in) is verified server-side on every request.

## Local setup

1. **Secrets (never committed).** Copy `.env.example` to `.env` and set real random passwords:
   ```
   DB_PASSWORD=...
   PGADMIN_PASSWORD=...
   ```
2. **Database.** `docker compose up -d` starts PostgreSQL (`localhost:5432`) and pgAdmin (`http://localhost:5050`).
   Data lives in the named volume `jewelry_manager_db_data` and survives restarts (`down -v` deletes it).
3. **App config.** Create `JewelryManager.Api/appsettings.Development.json` (git-ignored):
   ```json
   {
     "ConnectionStrings": {
       "Default": "Host=localhost;Port=5432;Database=jewelry_manager_dotnet;Username=jewelry;Password=<DB_PASSWORD>"
     },
     "Seed": { "OwnerEmail": "<your Google email>" }
   }
   ```
4. **Firebase.** Put the service-account key at `JewelryManager.Api/firebase-adminsdk.json` (git-ignored).
5. **Run.** `dotnet run --project JewelryManager.Api` → `http://localhost:3000`.
   In Development the app applies migrations and seeds default data on startup.

## Tests

`dotnet test` — the integration tests use a real PostgreSQL database `jewelry_manager_test`
(password read from `.env`, or set `TEST_DB_CONNECTION`).

## Layout

`Features/<Domain>/` holds each feature's Controller, Service and `Dtos/`. Controllers are HTTP-only,
services hold the business logic, and `AppDbContext` is the only code that talks to the database.
Every query on a business-scoped entity filters by the caller's `BusinessId`.
