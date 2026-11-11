# Assumptions

## Where the brief was silent

- **Identity**: no login. `X-Student-Id` is trusted as the identity (any non-empty string up to 100 chars). A real system needs authentication.
- **Seat counting**: seats used = CONFIRMED holds + HELD holds that have not expired. Cancelled and expired holds use no seat.
- **Expiry is computed, not scheduled**: a HELD hold whose `expiresAt` has passed is treated as EXPIRED everywhere (counts, `GET /holds`, `/me/holds`, confirm). No background job and no API call is needed to free the seat, and nothing can go wrong if the app was down at the moment of expiry. The stored row stays `HELD`; only the reported status changes.
- **One active application per student per class**: a second apply while one is HELD or CONFIRMED returns 409 `ALREADY_APPLIED`. After expiry or cancel the student can apply again. A student may apply to different classes.
- **Confirm**: only the student who owns the hold can confirm (403 otherwise). Confirming twice returns 200 and changes nothing (idempotent). Confirming an expired hold returns 409 `HOLD_EXPIRED` even if the timer expired a moment ago. Confirm does not change the seat total (HELD becomes CONFIRMED).
- **Cancel** (`DELETE /holds/{id}`): owner only. HELD becomes CANCELLED (seat released). Already CANCELLED or EXPIRED returns 204 (no-op). A CONFIRMED admission cannot be cancelled by this endpoint (409), because the brief has no refund flow.
- **Status codes**: creates (`POST /classes`, `POST .../holds`) return 201. Full class returns 409. Missing header returns 400. Unknown ids return 404.
- **Response fields**: the class JSON includes both `heldSeats` and `holdSeats` (same value) because the brief's spelling was not clear to me; extra fields are harmless.
- **Ids** are short strings such as `c_3fa9...` and `h_71bc...`.
- **Payment** is fake: the "Pay admission fee" button just calls confirm.
- **Clock**: the countdown uses the server's `expiresAt` against the browser clock. If a parent's clock is wrong, the timer display can be off, but the server is always the authority (confirm is checked against server time).
- **Schema**: created with `EnsureCreated` at startup (retries until PostgreSQL is ready) instead of EF migrations, to keep the project small. A production system should use migrations.
- **CORS** is open to any origin, because the browser calls the API directly on another port.

## Bonus: two backend processes, one database

Concurrency is enforced **in PostgreSQL, not in application memory**, so it holds with any number of backend processes or servers.

Applying runs in a transaction that first does `SELECT ... FROM "Classes" WHERE "Id" = @id FOR UPDATE`. That row lock makes all apply requests for one class queue up. Inside the lock the code counts active seats and checks the student's existing application, then inserts the hold and commits. The next request only gets the lock after the commit, and under READ COMMITTED its next statement sees the new row. So `held + confirmed` can never pass `totalSeats`, and a double-click from one student cannot create two holds.

- A plain "count then insert" without the lock would let two processes both see one free seat; an in-process `lock` or semaphore would not help across processes. Both are avoided.
- Confirm and cancel lock the single hold row (`FOR UPDATE`), so ten parallel confirms serialise and only the first changes the row.
- Locks are per class, so different classes do not block each other.
- Trade-off: a very hot class serialises its applies. Each critical section is a few short queries, which is fine for thousands of parents per class. If it ever became a bottleneck, the next step would be a per-class counter row or a queue.
