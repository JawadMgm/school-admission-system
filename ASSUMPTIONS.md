# Assumptions

Things the brief leaves open, what I chose, and why.

## Behaviour

- **Identity**: no login, as the brief says. `X-Student-Id` must be exactly 17 digits (birth registration number); anything else gets `400 INVALID_STUDENT_ID`. The value is trusted as-is; a real system would verify it.
- **Seats used** = CONFIRMED holds + HELD holds that have not expired. CANCELLED and EXPIRED holds use no seat.
- **Expiry is computed, not scheduled.** A HELD hold whose `expiresAt` has passed is reported as `EXPIRED` everywhere (seat counts, `GET /holds/{id}`, `/me/holds`, confirm). No background job and no API call is needed to free the seat, and nothing is missed if the app was down at the moment of expiry. The row in the database stays `HELD`; only the reported status changes.
- **One active application per student per class**: a second apply while one is HELD or CONFIRMED returns `409 ALREADY_APPLIED`. After expiry or cancel the student may apply again. A student may apply to different classes at the same time.
- **Cancel** (`DELETE /holds/{id}`): only the owner (403 otherwise). HELD becomes CANCELLED and the seat is freed. Cancelling an already CANCELLED or EXPIRED hold is a harmless `204`. A CONFIRMED admission cannot be cancelled here (`409`), because the brief has no refund flow.
- **Confirm by someone else** returns `403`. Confirming an expired or cancelled hold returns `409`. Confirming an already confirmed hold returns `200` again and changes nothing.
- **`GET /holds/{id}`** needs no header (the brief shows none); it reveals only ids, status and expiry.
- **Errors** use the body `{ "error": { "code": "...", "message": "..." } }`. The brief does not define an error format; this gives the UI a stable code to translate into a friendly message.
- **Extra response fields**: the brief allows extra fields. `GET /holds/{id}` also returns `studentId`, and `POST /classes` returns the seat counts.
- **Class name**: required, max 200 characters. **totalSeats**: whole number from 1 to 100000.
- **Payment** is fake, as the brief allows: "ভর্তি ফি দিন" simply calls confirm.
- **Countdown** uses the server's `expiresAt` against the browser clock. The UI switches to EXPIRED and hides the pay button by itself when the time passes; the server is still the authority (it rejects late confirms).
- **Live updates**: the UI polls the API every 5 seconds for seat counts and applications.
- **Schema** is created with `EnsureCreated` at startup (retrying until PostgreSQL is ready) instead of EF migrations, to keep the project small. Data lives in a named Docker volume, so `docker compose down` and `up` keep it.
- **CORS** is open, because the browser calls the API directly on another port.

## Bonus: two backend processes, one database

Seat safety is enforced **in PostgreSQL, not in application memory**, so it holds for any number of backend processes or servers.

Applying runs in one transaction that first runs `SELECT ... FROM "Classes" WHERE "Id" = @id FOR UPDATE`. This row lock makes all apply requests for the same class wait in line, whichever process they hit. Inside the lock the code counts active seats and checks the student's existing application, then inserts the hold and commits. The next request gets the lock only after that commit and, under READ COMMITTED, sees the new row. So `held + confirmed` can never pass `totalSeats`, and a double click cannot create two holds.

- A plain "count, then insert" without the lock would let two processes both see the last free seat, and an in-process `lock` cannot coordinate separate processes. The database lock avoids both.
- Confirm and cancel lock the single hold row (`FOR UPDATE`), so parallel confirms of the same hold run one at a time and only the first changes it; `confirmedSeats` rises once.
- Locks are per class, so different classes never block each other.
- Verified by running two backend processes on ports 8080 and 8081 against one database and sending 100 parallel applies to a 20-seat class: exactly 20 succeeded and 80 got 409.
- Trade-off: a very popular class serialises its applies. Each critical section is a few short queries, which is fine at the scale of thousands of parents per class.
