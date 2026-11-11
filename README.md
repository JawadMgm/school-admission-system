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

## API

All student endpoints need header `X-Student-Id`. Errors look like `{ "error": { "code": "CLASS_FULL", "message": "..." } }`.

| Method & path | Purpose | Success | Errors |
|---|---|---|---|
| `POST /classes` | create class `{name,totalSeats}` | 201 | 400 |
| `GET /classes` | list with held / confirmed / available seats | 200 | |
| `GET /classes/{id}` | one class | 200 | 404 |
| `POST /classes/{id}/holds` | apply (hold a seat) | 201 | 400, 404, 409 `CLASS_FULL` / `ALREADY_APPLIED` |
| `GET /holds/{id}` | hold status: HELD, CONFIRMED, EXPIRED, CANCELLED | 200 | 404 |
| `POST /holds/{id}/confirm` | pay and confirm (idempotent) | 200 | 403, 404, 409 `HOLD_EXPIRED` |
| `DELETE /holds/{id}` | cancel a hold | 204 | 403, 404, 409 (already confirmed) |
| `GET /me/holds` | my applications | 200 | 400 |

## Tests

Backend (needs Docker: starts a throwaway PostgreSQL with Testcontainers):

```bash
cd backend
dotnet test
```

Or against an existing database: set `TEST_DB_CONNECTION="Host=localhost;Username=...;Password=...;Database=..."`.

The concurrency tests fire 50 parallel requests at a 10-seat class and assert exactly 10 succeed, plus parallel confirms and same-student duplicates.

Frontend:

```bash
cd frontend
npm install
npm test
```

## Local development without Docker

```bash
cd backend/SchoolAdmission.Api
dotnet run --urls http://localhost:8080      # needs ConnectionStrings__Default set
cd frontend && npm install && npm run dev    # http://localhost:3000
```

See `ASSUMPTIONS.md` for the decisions the brief left open and how concurrency is handled, and `AI_USAGE.md` for AI usage.
