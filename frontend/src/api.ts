export type Module = { code: string; name: string; group: string }
export type Me = {
  id: string
  name: string
  email: string
  theme: 'light' | 'dark' | null
  role: string
  company: { name: string; currency: string }
  modules: Module[]
}

export class ApiError extends Error {
  status: number
  errors?: Record<string, string[]>
  constructor(status: number, message: string, errors?: Record<string, string[]>) {
    super(message)
    this.status = status
    this.errors = errors
  }
}

export function errorText(err: unknown) {
  const a = err as ApiError
  return a.errors ? Object.values(a.errors).flat().join(' ') : a.message
}

export async function api<T = void>(path: string, init?: { method?: string; body?: unknown }): Promise<T> {
  const res = await fetch(`/api${path}`, {
    method: init?.method ?? 'GET',
    headers: init?.body ? { 'Content-Type': 'application/json' } : undefined,
    body: init?.body ? JSON.stringify(init.body) : undefined,
  })
  if (!res.ok) {
    const p = await res.json().catch(() => ({}))
    throw new ApiError(res.status, p.detail ?? p.title ?? `Error ${res.status}`, p.errors)
  }
  return res.status === 204 || res.headers.get('content-length') === '0' ? (undefined as T) : res.json()
}
