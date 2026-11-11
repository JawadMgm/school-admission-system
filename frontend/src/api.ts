declare global {
  interface Window {
    __CONFIG__?: { API_URL?: string };
  }
}

// Runtime config (config.js, written by the container from the API_URL env var) wins over build-time env.
const baseUrl = () =>
  window.__CONFIG__?.API_URL || import.meta.env.VITE_API_URL || 'http://localhost:8080';

export type ClassInfo = {
  id: string;
  name: string;
  totalSeats: number;
  heldSeats: number;
  confirmedSeats: number;
  availableSeats: number;
};

export type HoldStatus = 'HELD' | 'CONFIRMED' | 'EXPIRED' | 'CANCELLED';

export type MyHold = {
  holdId: string;
  classId: string;
  className: string;
  status: HoldStatus;
  expiresAt: string;
};

export class ApiError extends Error {
  constructor(public status: number, public code: string, message: string) {
    super(message);
  }
}

const FRIENDLY: Record<string, string> = {
  CLASS_FULL: 'Sorry, no seats are left in this class.',
  ALREADY_APPLIED: 'You already have an active application for this class.',
  HOLD_EXPIRED: 'Your seat hold has expired. Please apply again.',
  HOLD_CANCELLED: 'This application was cancelled. Please apply again.',
  ALREADY_CONFIRMED: 'This admission is already confirmed.',
  FORBIDDEN: 'This application belongs to a different student ID.',
  INVALID_STUDENT_ID: 'Please enter a valid 17-digit birth registration number.',
};

async function request<T>(
  path: string,
  opts: { method?: string; studentId?: string; body?: unknown } = {},
): Promise<T> {
  const headers: Record<string, string> = {};
  if (opts.studentId) headers['X-Student-Id'] = opts.studentId;
  if (opts.body !== undefined) headers['Content-Type'] = 'application/json';

  let res: Response;
  try {
    res = await fetch(baseUrl() + path, {
      method: opts.method ?? 'GET',
      headers,
      body: opts.body !== undefined ? JSON.stringify(opts.body) : undefined,
    });
  } catch {
    throw new ApiError(0, 'NETWORK', 'Cannot reach the server. Check your connection and try again.');
  }

  if (res.status === 204) return undefined as T;
  const data = await res.json().catch(() => null);
  if (!res.ok) {
    const code: string = data?.error?.code ?? 'UNKNOWN';
    throw new ApiError(res.status, code, FRIENDLY[code] ?? data?.error?.message ?? 'Something went wrong. Please try again.');
  }
  return data as T;
}

export const api = {
  listClasses: () => request<ClassInfo[]>('/classes'),
  createClass: (name: string, totalSeats: number) =>
    request<ClassInfo>('/classes', { method: 'POST', body: { name, totalSeats } }),
  apply: (classId: string, studentId: string) =>
    request<{ holdId: string }>(`/classes/${classId}/holds`, { method: 'POST', studentId }),
  confirm: (holdId: string, studentId: string) =>
    request<{ holdId: string; status: HoldStatus }>(`/holds/${holdId}/confirm`, { method: 'POST', studentId }),
  cancel: (holdId: string, studentId: string) =>
    request<void>(`/holds/${holdId}`, { method: 'DELETE', studentId }),
  myHolds: (studentId: string) => request<MyHold[]>('/me/holds', { studentId }),
};
