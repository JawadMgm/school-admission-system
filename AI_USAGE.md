# AI Usage

**Tool**: Claude Code (Anthropic; models Claude Sonnet 5.5 and Claude Opus 5.5), run in a terminal session on my machine.

**What it did**: from the assignment it designed the API and the concurrency approach and wrote almost all of the code and docs: the .NET backend, the xUnit integration and concurrency tests, the React frontend and its tests, both Dockerfiles, `docker-compose.yml`, and these documents.

**What I did myself**: gave it the assignment, directed the work, checked its output against the brief, and I am responsible for reviewing and understanding everything I submit.

## Where the AI got things wrong, and how it was caught

- **API contract: `GET /me/holds` had the wrong shape.** The first version returned `{ "holds": [ ... ] }`; the brief requires a plain JSON array. Caught by comparing every endpoint line by line against the full brief. Fixed in the backend and the frontend, and a test now asserts the response is an array.
- **Student ID not validated.** The first version accepted any text in `X-Student-Id`, but the brief says it is a 17-digit birth registration number and that bad input must get a 4xx. Caught in the same review. It now returns 400 for anything that is not 17 digits, and the UI checks it before calling the API.
- **Guessed field name.** Working from a blurry screenshot, it returned both `heldSeats` and an invented `holdSeats`. Removed once the full brief confirmed `heldSeats`.
- **Frontend test mock**: the first `App.test.tsx` mock treated the `GET` request as "no method" and returned 404, so 3 of 4 tests failed. Seen in the `vitest` output and fixed.
- **Frontend tests leaked between runs**: Testing Library's automatic cleanup does not run without vitest globals, so components from earlier tests stayed on the page and a new test found duplicate buttons. Fixed with an explicit `afterEach(cleanup)`.
- **Local build quirk**: `npm run build` / `npm test` failed because the folder path contains `&`, which breaks npm's Windows shell. Worked around by running the tools through `node` directly (a local path problem only; Docker is unaffected).

## How it was verified

The development machine had no Docker, so the AI installed the .NET 8 SDK and a portable PostgreSQL locally:

- the backend builds with no warnings, and all 10 backend tests and 6 frontend tests pass;
- two backend processes (ports 8080 and 8081) sharing one database gave exactly 20 seats out of 100 parallel applies for a 20-seat class;
- a probe of 20+ bad inputs (malformed JSON, wrong types, bad IDs, unknown ids) returned only 4xx, never 500;
- the UI flow (apply, countdown, pay, confirmed) worked in a browser against the live API.

The Docker images and `docker compose up` were not run on that machine.
