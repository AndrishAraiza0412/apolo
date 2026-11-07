import { useEffect, useState, type FormEvent } from 'react'
import { api, errorText } from './api'
import { fmtDate, iso } from './totals'

type Project = {
  id: string; name: string; customerId: string | null; customer: string | null; status: string; budget: number
  startDate: string | null; dueDate: string | null; notes: string | null; tasks: number; done: number
}
type Task = { id: string; title: string; done: boolean; dueDate: string | null; assigneeId: string | null; assignee: string | null }
type Catalog = { customers: { id: string; name: string }[]; users: { id: string; name: string }[] }

const STATUS: Record<string, { label: string; cls: string }> = {
  activo: { label: 'Activo', cls: '' },
  pausado: { label: 'Pausado', cls: 'muted-pill' },
  cerrado: { label: 'Cerrado', cls: 'ok' },
}
const today = iso(new Date())

export default function Proyectos({ currency }: { currency: string }) {
  const money = new Intl.NumberFormat('es-MX', { style: 'currency', currency, maximumFractionDigits: 0 })
  const [projects, setProjects] = useState<Project[]>([])
  const [catalog, setCatalog] = useState<Catalog>({ customers: [], users: [] })
  const [selected, setSelected] = useState<string | 'new' | null>(null)

  const load = () => api<Project[]>('/projects').then(setProjects)
  useEffect(() => { load(); api<Catalog>('/projects/catalog').then(setCatalog) }, [])

  const active = projects.filter(p => p.status === 'activo')
  const current = projects.find(p => p.id === selected)

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Proyectos</h1>
          <div className="muted">{active.length} activos · presupuesto {money.format(active.reduce((s, p) => s + p.budget, 0))}</div>
        </div>
        <button className="btn" onClick={() => setSelected(selected === 'new' ? null : 'new')}>{selected === 'new' ? 'Cancelar' : 'Nuevo proyecto'}</button>
      </div>

      <div className={selected ? 'split' : ''}>
        <div className="card">
          <table>
            <thead>
              <tr><th>Proyecto</th><th>Cliente</th><th>Entrega</th><th className="num">Presupuesto</th><th>Avance</th><th>Estado</th></tr>
            </thead>
            <tbody>
              {projects.map(p => {
                const pct = p.tasks ? Math.round((p.done / p.tasks) * 100) : 0
                const late = p.status === 'activo' && p.dueDate !== null && p.dueDate < today
                return (
                  <tr key={p.id} tabIndex={0} className="clickable" aria-selected={selected === p.id}
                    onClick={() => setSelected(p.id)} onKeyDown={e => e.key === 'Enter' && setSelected(p.id)}>
                    <td>{p.name}</td>
                    <td>{p.customer ?? <span className="muted">Interno</span>}</td>
                    <td className={late ? 'error' : ''}>{fmtDate(p.dueDate)}</td>
                    <td className="num">{money.format(p.budget)}</td>
                    <td>
                      <div className="progress" role="progressbar" aria-valuenow={pct} aria-valuemin={0} aria-valuemax={100} aria-label={`Avance ${pct}%`}>
                        <span style={{ width: `${pct}%` }} />
                      </div>
                      <div className="muted">{p.done}/{p.tasks} tareas</div>
                    </td>
                    <td><span className={`pill ${STATUS[p.status].cls}`}>{STATUS[p.status].label}</span></td>
                  </tr>
                )
              })}
              {!projects.length && <tr><td colSpan={6} className="empty">Sin proyectos. Crea uno con «Nuevo proyecto».</td></tr>}
            </tbody>
          </table>
        </div>

        {selected && (
          <div className="card stack">
            <div className="detail-head">
              <p className="section-title flush">{current ? current.name : 'Nuevo proyecto'}</p>
              <button className="icon-btn" onClick={() => setSelected(null)} aria-label="Cerrar">×</button>
            </div>
            <ProjectForm key={selected} p={current} catalog={catalog} onSaved={id => { load(); setSelected(id) }} />
            {current && <Tasks key={current.id} projectId={current.id} users={catalog.users} onChanged={load} />}
          </div>
        )}
      </div>
    </>
  )
}

function ProjectForm({ p, catalog, onSaved }: { p?: Project; catalog: Catalog; onSaved: (id: string) => void }) {
  const [error, setError] = useState('')
  const [saved, setSaved] = useState(false)

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const f = Object.fromEntries(new FormData(e.currentTarget)) as Record<string, string>
    const body = {
      ...f, budget: Number(f.budget), customerId: f.customerId || null,
      startDate: f.startDate || null, dueDate: f.dueDate || null, status: f.status ?? 'activo',
    }
    setError(''); setSaved(false)
    try {
      if (p) { await api(`/projects/${p.id}`, { method: 'PUT', body }); setSaved(true); onSaved(p.id) }
      else onSaved((await api<{ id: string }>('/projects', { method: 'POST', body })).id)
    } catch (err) { setError(errorText(err)) }
  }

  return (
    <form onSubmit={submit} className="form-grid">
      <label className="wide">Nombre<input name="name" defaultValue={p?.name} required /></label>
      <label>Cliente
        <select name="customerId" defaultValue={p?.customerId ?? ''}>
          <option value="">Interno</option>
          {catalog.customers.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
        </select>
      </label>
      <label>Presupuesto<input name="budget" type="number" min="0" step="0.01" defaultValue={p?.budget ?? 0} /></label>
      <label>Inicio<input name="startDate" type="date" defaultValue={p?.startDate ?? ''} /></label>
      <label>Entrega<input name="dueDate" type="date" defaultValue={p?.dueDate ?? ''} /></label>
      {p && (
        <label>Estado
          <select name="status" defaultValue={p.status}>
            {Object.entries(STATUS).map(([k, v]) => <option key={k} value={k}>{v.label}</option>)}
          </select>
        </label>
      )}
      <label className="wide">Notas<input name="notes" defaultValue={p?.notes ?? ''} /></label>
      <button className="btn">{p ? 'Guardar' : 'Crear proyecto'}</button>
      {saved && <span className="muted" role="status">Guardado ✓</span>}
      {error && <p className="error wide" role="alert">{error}</p>}
    </form>
  )
}

function Tasks({ projectId, users, onChanged }: { projectId: string; users: Catalog['users']; onChanged: () => void }) {
  const [tasks, setTasks] = useState<Task[]>([])
  const [error, setError] = useState('')
  const load = () => api<Task[]>(`/projects/${projectId}/tasks`).then(setTasks)
  useEffect(() => { load() }, [projectId]) // eslint-disable-line react-hooks/exhaustive-deps

  async function call(fn: () => Promise<unknown>) {
    setError('')
    try { await fn(); await load(); onChanged() } catch (err) { setError(errorText(err)) }
  }

  const update = (t: Task, patch: Partial<Task>) => call(() =>
    api(`/projects/${projectId}/tasks/${t.id}`, { method: 'PUT', body: { ...t, ...patch } }))

  async function add(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const form = e.currentTarget
    const f = Object.fromEntries(new FormData(form)) as Record<string, string>
    await call(() => api(`/projects/${projectId}/tasks`, {
      method: 'POST', body: { title: f.title, done: false, dueDate: f.dueDate || null, assigneeId: f.assigneeId || null },
    }))
    form.reset()
  }

  return (
    <div className="stack tight">
      <p className="section-title flush">Tareas</p>
      <ul className="task-list">
        {tasks.map(t => (
          <li key={t.id} className={t.done ? 'done' : ''}>
            <label className="check">
              <input type="checkbox" checked={t.done} onChange={e => update(t, { done: e.target.checked })} />
              <span>{t.title}</span>
            </label>
            <span className={`muted ${!t.done && t.dueDate && t.dueDate < today ? 'error' : ''}`}>
              {t.dueDate ? fmtDate(t.dueDate) : ''}{t.assignee ? ` · ${t.assignee}` : ''}
            </span>
            <button className="icon-btn" onClick={() => call(() => api(`/projects/${projectId}/tasks/${t.id}`, { method: 'DELETE' }))}
              aria-label={`Quitar ${t.title}`}>×</button>
          </li>
        ))}
        {!tasks.length && <li className="muted">Sin tareas todavía.</li>}
      </ul>
      <form onSubmit={add} className="form-grid">
        <label className="wide">Nueva tarea<input name="title" required /></label>
        <label>Fecha<input name="dueDate" type="date" /></label>
        <label>Responsable
          <select name="assigneeId" defaultValue="">
            <option value="">Sin asignar</option>
            {users.map(u => <option key={u.id} value={u.id}>{u.name}</option>)}
          </select>
        </label>
        <button className="btn secondary">Agregar</button>
      </form>
      {error && <p className="error" role="alert">{error}</p>}
    </div>
  )
}
