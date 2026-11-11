import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, expect, test, vi } from 'vitest';
import App, { formatRemaining } from './App';

const cls = { id: 'c_1', name: 'Class 6 - Morning Shift', totalSeats: 120, heldSeats: 15, confirmedSeats: 90, availableSeats: 15 };
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });

let applyResponse: Response;
let fetchMock: ReturnType<typeof vi.fn>;

beforeEach(() => {
  localStorage.clear();
  localStorage.setItem('studentId', 'stu-1');
  applyResponse = json({ holdId: 'h_1', status: 'HELD' }, 201);
  fetchMock = vi.fn(async (url: string, init?: RequestInit) => {
    if (url.endsWith('/classes') && init?.method === 'GET') return json([cls]);
    if (url.endsWith('/me/holds')) return json({ holds: [] });
    if (url.endsWith('/classes/c_1/holds')) return applyResponse;
    return json({}, 404);
  });
  vi.stubGlobal('fetch', fetchMock);
});

test('shows class with seat counts', async () => {
  render(<App />);
  expect(await screen.findByText('Class 6 - Morning Shift')).toBeInTheDocument();
  expect(screen.getByText('120')).toBeInTheDocument();
  expect(screen.getByRole('button', { name: 'Apply' })).toBeEnabled();
});

test('apply sends X-Student-Id header', async () => {
  render(<App />);
  await userEvent.click(await screen.findByRole('button', { name: 'Apply' }));
  await waitFor(() => {
    const call = fetchMock.mock.calls.find(([u]) => String(u).endsWith('/classes/c_1/holds'));
    expect(call?.[1].headers['X-Student-Id']).toBe('stu-1');
  });
  expect(await screen.findByText(/Seat held/)).toBeInTheDocument();
});

test('shows a friendly message when the class is full', async () => {
  applyResponse = json({ error: { code: 'CLASS_FULL', message: 'x' } }, 409);
  render(<App />);
  await userEvent.click(await screen.findByRole('button', { name: 'Apply' }));
  expect(await screen.findByRole('alert')).toHaveTextContent('no seats are left');
});

test('formats the countdown', () => {
  expect(formatRemaining(125_000)).toBe('2:05');
  expect(formatRemaining(-5)).toBe('0:00');
});
