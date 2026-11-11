# AI Usage

**Tool**: Claude Code (Anthropic, model Claude Sonnet 5.5), run in a terminal session on my machine.

**What it did**: from the assignment PDF it designed the API and concurrency approach and wrote almost all of the code and docs: the .NET backend, the xUnit integration and concurrency tests, the React frontend and its tests, both Dockerfiles, `docker-compose.yml`, and these documents.

**What I did myself**: gave it the assignment, and I am responsible for reading, running and understanding everything before submission. (Edit this line to describe your own part honestly, including anything you changed.)

## Where the AI got things wrong, and how it was caught

- **Frontend test mock**: the first version of `App.test.tsx` treated the `GET` request as "no method" and returned 404 for the class list, so 3 of 4 tests failed. Running `vitest` showed an error banner in the rendered output; the mock was fixed to match the explicit `GET` method.
- **Build scripts on this machine**: `npm run build` / `npm test` failed because the project sits under a folder whose name contains `&`, which breaks npm's Windows shell. The AI found this from the error and ran the tools through `node` directly. (This is a local-path problem, not a project problem; Docker and normal paths are fine.)
- **Not verified here**: the machine had no .NET SDK and no Docker, so the backend, backend tests and `docker compose up` were **not compiled or run by the AI**. Only the frontend was built and tested. These must be run before relying on them.
