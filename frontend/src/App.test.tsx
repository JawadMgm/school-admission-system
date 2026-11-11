import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, expect, test, vi } from 'vitest';
import App, { formatRemaining } from './App';

const STUDENT = '20152691234567890';
const cls = { id: 'c_1', name: 'Class 6 - Morning Shift', totalSeats: 120, heldSeats: 15, confirmedSeats: 80, availableSeats: 25 };
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });

let applyResponse: Response;
let myHolds: unknown[];
let fetchMock: ReturnType<typeof vi.fn>;

beforeEach(() => {
  localStorage.clear();
  localStorage.setItem('studentId', STUDENT);
  applyResponse = json({ holdId: 'h_1', status: 'HELD' }, 201);
  myHolds = [];
  fetchMock = vi.fn(async (url: string, init?: RequestInit) => {
    if (url.endsWith('/classes') && init?.method === 'GET') return json([cls]);
    if (url.endsWith('/me/holds')) return json(myHolds); // contract: plain array
    if (url.endsWith('/classes/c_1/holds')) return applyResponse;
    return json({}, 404);
  });
  vi.stubGlobal('fetch', fetchMock);
});

test('shows class with seat counts', async () => {
  render(<App />);
  expect(await screen.findByText('Class 6 - Morning Shift')).toBeInTheDocument();
  expect(screen.getByText('120')).toBeInTheDocument();
  expect(screen.getByRole('button', { name: 'আবেদন করুন' })).toBeEnabled();
});

test('apply sends X-Student-Id header', async () => {
  render(<App />);
  await userEvent.click(await screen.findByRole('button', { name: 'আবেদন করুন' }));
  await waitFor(() => {
    const call = fetchMock.mock.calls.find(([u]) => String(u).endsWith('/classes/c_1/holds'));
    expect(call?.[1].headers['X-Student-Id']).toBe(STUDENT);
  });
  expect(await screen.findByText(/Seat held/)).toBeInTheDocument();
});

test('shows a friendly message when the class is full', async () => {
  applyResponse = json({ error: { code: 'CLASS_FULL', message: 'x' } }, 409);
  render(<App />);
  await userEvent.click(await screen.findByRole('button', { name: 'আবেদন করুন' }));
  expect(await screen.findByRole('alert')).toHaveTextContent('no seats are left');
});

test('an expired hold shows EXPIRED and no pay button', async () => {
  localStorage.setItem('tab', 'mine');
  myHolds = [{ holdId: 'h_1', classId: 'c_1', className: 'Class 6 - Morning Shift', status: 'HELD', expiresAt: '2000-01-01T00:00:00Z' }];
  render(<App />);
  expect(await screen.findByText('EXPIRED')).toBeInTheDocument();
  expect(screen.queryByRole('button', { name: 'ভর্তি ফি দিন' })).not.toBeInTheDocument();
});

test('rejects a student ID that is not 17 digits without calling the API', async () => {
  localStorage.setItem('studentId', '123');
  render(<App />);
  expect(await screen.findByText('Must be exactly 17 digits.')).toBeInTheDocument();
  await userEvent.click(await screen.findByRole('button', { name: 'আবেদন করুন' }));
  expect(await screen.findByRole('alert')).toHaveTextContent('17-digit');
  expect(fetchMock.mock.calls.some(([u]) => String(u).endsWith('/holds'))).toBe(false);
});

test('formats the countdown', () => {
  expect(formatRemaining(125_000)).toBe('2:05');
  expect(formatRemaining(-5)).toBe('0:00');
});
