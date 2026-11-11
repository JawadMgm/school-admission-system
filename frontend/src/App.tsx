import { useCallback, useEffect, useState } from 'react';
import { api, ApiError, ClassInfo, MyHold } from './api';

type Tab = 'classes' | 'mine' | 'admin';

const store = {
  get: (k: string) => {
    try { return localStorage.getItem(k) ?? ''; } catch { return ''; }
  },
  set: (k: string, v: string) => {
    try { localStorage.setItem(k, v); } catch { /* storage unavailable: app still works */ }
  },
};

export function formatRemaining(ms: number): string {
  const s = Math.max(0, Math.ceil(ms / 1000));
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
}

export default function App() {
  const [studentId, setStudentId] = useState(() => store.get('studentId'));
  const [tab, setTab] = useState<Tab>(() => (store.get('tab') as Tab) || 'classes');
  const [classes, setClasses] = useState<ClassInfo[] | null>(null);
  const [holds, setHolds] = useState<MyHold[] | null>(null);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [busy, setBusy] = useState(false);
  const [now, setNow] = useState(Date.now());

  const sid = studentId.trim();

  const loadClasses = useCallback(async () => {
    try { setClasses(await api.listClasses()); }
    catch (e) { setError(e instanceof ApiError ? e.message : 'Could not load classes.'); }
  }, []);

  const loadHolds = useCallback(async () => {
    if (!sid) { setHolds([]); return; }
    try { setHolds(await api.myHolds(sid)); }
    catch (e) { setError(e instanceof ApiError ? e.message : 'Could not load your applications.'); }
  }, [sid]);

  useEffect(() => {
    loadClasses();
    loadHolds();
    const poll = setInterval(() => { loadClasses(); loadHolds(); }, 5000);
    return () => clearInterval(poll);
  }, [loadClasses, loadHolds]);

  useEffect(() => {
    const tick = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(tick);
  }, []);

  const changeTab = (t: Tab) => { setTab(t); store.set('tab', t); setError(''); setNotice(''); };
  const changeStudent = (v: string) => { setStudentId(v); store.set('studentId', v); };

  async function run(action: () => Promise<void>) {
    setError(''); setNotice(''); setBusy(true);
    try { await action(); }
    catch (e) { setError(e instanceof ApiError ? e.message : 'Something went wrong. Please try again.'); }
    finally {
      setBusy(false);
      await Promise.all([loadClasses(), loadHolds()]);
    }
  }

  const apply = (classId: string) => run(async () => {
    if (!sid) throw new ApiError(400, 'MISSING_STUDENT_ID', 'Please enter your student ID first.');
    await api.apply(classId, sid);
    changeTab('mine');
    setNotice('Seat held! Pay the admission fee before the timer runs out.');
  });

  const pay = (holdId: string) => run(async () => {
    await api.confirm(holdId, sid);
    setNotice('Payment received. Your admission is confirmed.');
  });

  const cancel = (holdId: string) => run(async () => {
    await api.cancel(holdId, sid);
    setNotice('Application cancelled. The seat was released.');
  });

  const status = (h: MyHold) =>
    h.status === 'HELD' && Date.parse(h.expiresAt) <= now ? 'EXPIRED' : h.status;

  return (
    <div className="app">
      <header>
        <h1>School Admission</h1>
        <label className="student">
          Student ID
          <input
            value={studentId}
            onChange={(e) => changeStudent(e.target.value)}
            placeholder="e.g. 2015261234567890"
          />
        </label>
      </header>

      <nav>
        {(['classes', 'mine', 'admin'] as Tab[]).map((t) => (
          <button key={t} className={tab === t ? 'active' : ''} onClick={() => changeTab(t)}>
            {{ classes: 'Classes', mine: 'My applications', admin: 'Admin: new class' }[t]}
          </button>
        ))}
      </nav>

      {error && <div className="banner error" role="alert">{error}</div>}
      {notice && <div className="banner ok" role="status">{notice}</div>}

      {tab === 'classes' && (
        <section>
          {classes === null && <p>Loading classes...</p>}
          {classes?.length === 0 && <p>No classes yet. Create one from the Admin tab.</p>}
          {classes?.map((c) => (
            <article key={c.id} className="card">
              <h2>{c.name}</h2>
              <dl className="seats">
                <div><dt>Total</dt><dd>{c.totalSeats}</dd></div>
                <div><dt>Held</dt><dd>{c.heldSeats}</dd></div>
                <div><dt>Confirmed</dt><dd>{c.confirmedSeats}</dd></div>
                <div><dt>Available</dt><dd>{c.availableSeats}</dd></div>
              </dl>
              <button disabled={busy || c.availableSeats === 0} onClick={() => apply(c.id)}>
                {c.availableSeats === 0 ? 'Full' : 'Apply'}
              </button>
            </article>
          ))}
        </section>
      )}

      {tab === 'mine' && (
        <section>
          {!sid && <p>Enter your student ID above to see your applications.</p>}
          {sid && holds === null && <p>Loading...</p>}
          {sid && holds?.length === 0 && <p>You have no applications yet.</p>}
          {holds?.map((h) => {
            const st = status(h);
            return (
              <article key={h.holdId} className="card">
                <h2>{h.className}</h2>
                <p><span className={`badge ${st}`}>{st}</span></p>
                {st === 'HELD' && (
                  <>
                    <p className="timer">Time left: <strong>{formatRemaining(Date.parse(h.expiresAt) - now)}</strong></p>
                    <button disabled={busy} onClick={() => pay(h.holdId)}>Pay admission fee</button>{' '}
                    <button className="secondary" disabled={busy} onClick={() => cancel(h.holdId)}>Cancel</button>
                    <p className="hint">Demo only: no real payment is taken.</p>
                  </>
                )}
                {st === 'EXPIRED' && (
                  <>
                    <p>This hold expired and the seat was released.</p>
                    <button disabled={busy} onClick={() => apply(h.classId)}>Apply again</button>
                  </>
                )}
                {st === 'CANCELLED' && (
                  <button disabled={busy} onClick={() => apply(h.classId)}>Apply again</button>
                )}
                {st === 'CONFIRMED' && <p>Admission confirmed.</p>}
              </article>
            );
          })}
        </section>
      )}

      {tab === 'admin' && <AdminForm onCreated={(c) => { setNotice(`Created "${c.name}".`); loadClasses(); changeTabKeepNotice('classes'); }} />}
    </div>
  );

  function changeTabKeepNotice(t: Tab) { setTab(t); store.set('tab', t); }
}

function AdminForm({ onCreated }: { onCreated: (c: ClassInfo) => void }) {
  const [name, setName] = useState('');
  const [seats, setSeats] = useState('');
  const [err, setErr] = useState('');
  const [saving, setSaving] = useState(false);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setErr('');
    const n = Number(seats);
    if (!name.trim()) return setErr('Class name is required.');
    if (!Number.isInteger(n) || n < 1) return setErr('Total seats must be a whole number of at least 1.');
    setSaving(true);
    try {
      onCreated(await api.createClass(name.trim(), n));
      setName(''); setSeats('');
    } catch (e2) {
      setErr(e2 instanceof ApiError ? e2.message : 'Could not create the class.');
    } finally { setSaving(false); }
  }

  return (
    <form className="card" onSubmit={submit}>
      <h2>Create a class</h2>
      <label>Class name<input value={name} onChange={(e) => setName(e.target.value)} placeholder="Class 6 - Morning Shift" /></label>
      <label>Total seats<input value={seats} onChange={(e) => setSeats(e.target.value)} inputMode="numeric" placeholder="120" /></label>
      {err && <div className="banner error" role="alert">{err}</div>}
      <button type="submit" disabled={saving}>Create class</button>
    </form>
  );
}
