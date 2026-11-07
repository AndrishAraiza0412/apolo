import { useEffect, useState, type FormEvent } from 'react'
import { api, ApiError, errorText } from './api'
import { useSat } from './sat'

type User = { id: string; name: string; email: string; role: string; active: boolean }
type Role = { id: string; name: string; modules: string[] }
type Audit = { id: string; createdAt: string; action: string; entity: string; changes: string | null; user: string | null }

const fmt = new Intl.DateTimeFormat('es-MX', { dateStyle: 'medium', timeStyle: 'short' })

export default function Configuracion() {
  const [tab, setTab] = useState<'usuarios' | 'empresa' | 'bitacora'>('usuarios')
  return (
    <>
      <div className="page-head">
        <div>
          <h1>Configuración</h1>
          <div className="muted">Usuarios, datos fiscales de la empresa y bitácora de auditoría.</div>
        </div>
      </div>
      <div className="tabs" role="tablist">
        <button role="tab" aria-selected={tab === 'usuarios'} onClick={() => setTab('usuarios')}>Usuarios</button>
        <button role="tab" aria-selected={tab === 'empresa'} onClick={() => setTab('empresa')}>Empresa</button>
        <button role="tab" aria-selected={tab === 'bitacora'} onClick={() => setTab('bitacora')}>Bitácora</button>
      </div>
      {tab === 'usuarios' ? <Usuarios /> : tab === 'empresa' ? <Empresa /> : <Bitacora />}
    </>
  )
}

function Usuarios() {
  const [users, setUsers] = useState<User[]>([])
  const [roles, setRoles] = useState<Role[]>([])
  const [error, setError] = useState('')

  const load = () => api<User[]>('/users').then(setUsers)
  useEffect(() => { load(); api<Role[]>('/roles').then(setRoles) }, [])

  async function create(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const form = e.currentTarget
    const f = Object.fromEntries(new FormData(form))
    setError('')
    try {
      await api('/users', { method: 'POST', body: f })
      form.reset()
      load()
    } catch (err) {
      const a = err as ApiError
      setError(a.errors ? Object.values(a.errors).flat().join(' ') : a.message)
    }
  }

  return (
    <div className="stack">
      <form className="card" onSubmit={create}>
        <p className="section-title">Nuevo usuario</p>
        <div className="form-row">
          <label>Nombre<input name="name" required /></label>
          <label>Correo<input name="email" type="email" required /></label>
          <label>Contraseña<input name="password" type="password" minLength={8} required autoComplete="new-password" /></label>
          <label>Rol
            <select name="roleId" required>
              {roles.map(r => <option key={r.id} value={r.id}>{r.name}</option>)}
            </select>
          </label>
          <button className="btn">Crear</button>
        </div>
        {error && <p className="error" role="alert">{error}</p>}
      </form>

      <div className="card">
        <table>
          <thead><tr><th>Nombre</th><th>Correo</th><th>Rol</th><th>Estado</th></tr></thead>
          <tbody>
            {users.map(u => (
              <tr key={u.id}>
                <td>{u.name}</td>
                <td>{u.email}</td>
                <td>{u.role}</td>
                <td><span className={`pill ${u.active ? 'ok' : ''}`}>{u.active ? 'Activo' : 'Inactivo'}</span></td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}

function Bitacora() {
  const [rows, setRows] = useState<Audit[]>([])
  useEffect(() => { api<Audit[]>('/audit').then(setRows) }, [])

  return (
    <div className="card">
      <table>
        <thead><tr><th>Fecha</th><th>Usuario</th><th>Acción</th><th>Entidad</th><th>Cambios</th></tr></thead>
        <tbody>
          {rows.map(r => (
            <tr key={r.id}>
              <td className="nowrap">{fmt.format(new Date(r.createdAt))}</td>
              <td>{r.user ?? <span className="muted">Sistema</span>}</td>
              <td><span className={`pill ${r.action === 'baja' ? 'bad' : ''}`}>{r.action}</span></td>
              <td>{r.entity}</td>
              <td><code>{r.changes}</code></td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

type Company = { name: string; rfc: string | null; legalName: string | null; taxRegime: string | null; zipCode: string | null; riskPremium: number }

// Con RFC la empresa factura electrónicamente (CFDI 4.0); sin RFC las facturas son documentos internos.
function Empresa() {
  const sat = useSat()
  const [c, setC] = useState<Company | null>(null)
  const [msg, setMsg] = useState({ error: '', ok: '' })
  useEffect(() => { api<Company>('/company').then(setC) }, [])
  if (!c || !sat) return null

  async function save(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const f = Object.fromEntries(new FormData(e.currentTarget)) as Record<string, string>
    const body = { ...f, riskPremium: Number(f.riskPremium) / 100 }
    setMsg({ error: '', ok: '' })
    try { await api('/company', { method: 'PUT', body }); setMsg({ error: '', ok: 'Guardado.' }) } catch (err) { setMsg({ error: errorText(err), ok: '' }) }
  }

  return (
    <form className="card stack" onSubmit={save}>
      <p className="section-title flush">Datos del emisor</p>
      <p className="muted flush">
        Con RFC capturado, cada factura emitida se timbra como CFDI 4.0. Modo actual: <strong>pruebas</strong> (sin PAC; el UUID no es válido ante el SAT).
      </p>
      <div className="form-grid">
        <label className="wide">Nombre comercial<input name="name" defaultValue={c.name} required /></label>
        <label className="wide">Razón social (sin régimen societario)<input name="legalName" defaultValue={c.legalName ?? ''} /></label>
        <label>RFC<input name="rfc" defaultValue={c.rfc ?? ''} maxLength={13} /></label>
        <label>Régimen fiscal
          <select name="taxRegime" defaultValue={c.taxRegime ?? ''}>
            <option value="">Sin capturar</option>
            {Object.entries(sat.regimes).map(([k, v]) => <option key={k} value={k}>{k} · {v}</option>)}
          </select>
        </label>
        <label>C.P. de expedición<input name="zipCode" defaultValue={c.zipCode ?? ''} inputMode="numeric" maxLength={5} /></label>
        <label>Prima de riesgo de trabajo (%)<input name="riskPremium" type="number" min="0.5" max="15" step="0.00001" defaultValue={+(c.riskPremium * 100).toFixed(5)} required /></label>
      </div>
      <div className="actions flush"><button className="btn">Guardar</button>{msg.ok && <span className="muted" role="status">{msg.ok}</span>}</div>
      {msg.error && <p className="error" role="alert">{msg.error}</p>}
    </form>
  )
}
