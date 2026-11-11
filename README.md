# School Admission System

First-come-first-served seat booking. A parent applies, the seat is **held** for a limited time, and paying the (demo) fee **confirms** it. If time runs out, the seat is free again automatically.

- Backend: C# / .NET 8 minimal API, EF Core, PostgreSQL (`backend/`)
- Frontend: React 18 + TypeScript + Vite (`frontend/`)

## Run (only Docker needed)

```bash
docker compose up --build
```

- Frontend: http://localhost:3000
- Backend API: http://localhost:8080
- Stop with `docker compose down`. Data survives (named volume). `docker compose down -v` wipes it.

### Configuration (environment variables)

| Variable | Where | Default | Meaning |
|---|---|---|---|
| `ConnectionStrings__Default` | backend | set in compose | PostgreSQL connection string |
| `HOLD_DURATION_SECONDS` | backend | `600` | How long a hold lasts. Try `HOLD_DURATION_SECONDS=30 docker compose up` to see expiry quickly |
| `API_URL` | frontend container | `http://localhost:8080` | Backend URL the browser calls (written to `config.js` at container start) |

## Using the UI

1. Type the student's 17-digit birth registration number in the box at the top.
2. **Admin: new class** creates a class (one per shift, e.g. "Class 6 - Morning Shift") with its seat count.
3. **Classes** shows total, held, confirmed and available seats (refreshes every few seconds). "আবেদন করুন" holds a seat.
4. **My applications** shows each application's status and a countdown, with "ভর্তি ফি দিন" (fake payment, confirms the seat) and "বাতিল" (cancel). When time runs out it turns EXPIRED by itself.

## API

Exactly the contract in the brief. All student endpoints need header `X-Student-Id` (17 digits). Errors look like `{ "error": { "code": "CLASS_FULL", "message": "..." } }`; bad input always gets a 4xx, never a 500.

| Method & path | Purpose | Success | Errors |
|---|---|---|---|
| `POST /classes` | create class `{name,totalSeats}` | 201 | 400 |
| `GET /classes` | list with `heldSeats` / `confirmedSeats` / `availableSeats` | 200 | |
| `GET /classes/{id}` | one class | 200 | 404 |
| `POST /classes/{id}/holds` | apply (hold a seat) | 201 | 400, 404, 409 `CLASS_FULL` / `ALREADY_APPLIED` |
| `GET /holds/{id}` | status: HELD, CONFIRMED, EXPIRED, CANCELLED | 200 | 404 |
| `POST /holds/{id}/confirm` | pay and confirm (idempotent) | 200 | 400, 403, 404, 409 `HOLD_EXPIRED` |
| `DELETE /holds/{id}` | cancel a hold | 204 | 400, 403, 404, 409 (already confirmed) |
| `GET /me/holds` | my applications (JSON array) | 200 | 400 |

## Tests

With Docker only:

```bash
docker compose --profile test run --rm backend-tests
docker compose --profile test run --rm frontend-tests
```

The backend tests use a separate `admission_test` database, so app data is untouched.

With the .NET 8 SDK / Node installed:

```bash
cd backend && dotnet test          # starts a throwaway PostgreSQL via Testcontainers (needs Docker)
cd frontend && npm install && npm test
```

To run the backend tests against an existing PostgreSQL instead, set `TEST_DB_CONNECTION="Host=...;Database=...;Username=...;Password=..."`.

What they cover: 10 backend tests, including the concurrency tests (50 parallel applies to a 10-seat class, exactly 10 succeed; 20 parallel applies by one student, exactly 1 succeeds; 10 parallel confirms, `confirmedSeats` rises once), expiry, ownership, the response contract and bad input. 6 frontend tests cover the class list, applying, friendly errors, auto-EXPIRED and ID validation.

## Local development without Docker

```bash
cd backend/SchoolAdmission.Api
dotnet run --urls http://localhost:8080      # needs ConnectionStrings__Default set
cd frontend && npm install && npm run dev    # http://localhost:3000
```

See `ASSUMPTIONS.md` for the decisions the brief left open and how concurrency is handled, and `AI_USAGE.md` for AI usage.
