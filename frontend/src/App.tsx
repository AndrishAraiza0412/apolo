import { useEffect, useState, type FormEvent } from 'react'
import { Navigate, NavLink, Route, Routes, useLocation } from 'react-router-dom'
import { api, ApiError, errorText, type Me, type Module } from './api'
import Configuracion from './Configuracion'
import Inventario from './Inventario'
import Ventas from './Ventas'
import Facturacion from './Facturacion'
import Dashboard from './Dashboard'
import Compras from './Compras'
import Proyectos from './Proyectos'
import Ia, { Copilot } from './Ia'
import Contabilidad from './Contabilidad'
import Rrhh from './Rrhh'
import Reportes from './Reportes'

const GROUPS = ['General', 'Operación', 'Finanzas', 'Sistema']

// Fase en la que se construye cada módulo (DESIGN.md §11).
const PHASE: Record<string, number> = {
  dashboard: 2, ia: 2, ventas: 2, facturacion: 2, inventario: 2,
  compras: 3, proyectos: 3, contabilidad: 4, rrhh: 4, reportes: 4,
}

function applyTheme(theme: 'light' | 'dark') {
  document.documentElement.dataset.theme = theme
  try { localStorage.setItem('apolo-theme', theme) } catch { /* sin almacenamiento: solo esta pestaña */ }
}

export default function App() {
  const [me, setMe] = useState<Me | null | undefined>(undefined)

  const load = () =>
    api<Me>('/me')
      .then(m => { if (m.theme) applyTheme(m.theme); setMe(m) })
      .catch(() => setMe(null))

  useEffect(() => { load() }, [])

  if (me === undefined) return null
  if (me === null) return <Login onDone={load} />
  return <Shell me={me} onLogout={() => api('/auth/logout', { method: 'POST' }).then(() => setMe(null))} />
}

function Login({ onDone }: { onDone: () => void }) {
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [setup, setSetup] = useState(false)
  useEffect(() => { api<{ needed: boolean }>('/setup').then(r => setSetup(r.needed)).catch(() => {}) }, [])
  if (setup) return <Setup onDone={() => setSetup(false)} />

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const f = new FormData(e.currentTarget)
    setBusy(true)
    setError('')
    try {
      await api('/auth/login', { method: 'POST', body: { email: f.get('email'), password: f.get('password') } })
      onDone()
    } catch (err) {
      setError(err instanceof ApiError && err.status === 429 ? 'Demasiados intentos. Espera un minuto.' : (err as Error).message)
      setBusy(false)
    }
  }

  return (
    <main className="login">
      <form className="card" onSubmit={submit}>
        <div className="logo">Apolo</div>
        <h1>Iniciar sesión</h1>
        <label>Correo<input name="email" autoComplete="username" required autoFocus /></label>
        <label>Contraseña<input name="password" type="password" autoComplete="current-password" required /></label>
        {error && <p className="error" role="alert">{error}</p>}
        <button className="btn" disabled={busy}>{busy ? 'Entrando…' : 'Entrar'}</button>
      </form>
    </main>
  )
}

// Instalación vacía: alta de la empresa y de su primer usuario (rol Gerencia) con el token que define quien despliega.
function Setup({ onDone }: { onDone: () => void }) {
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const body = Object.fromEntries(new FormData(e.currentTarget))
    if (body.password !== body.confirm) { setError('Las contraseñas no coinciden.'); return }
    setBusy(true)
    setError('')
    try {
      await api('/setup', { method: 'POST', body })
      onDone()
    } catch (err) {
      setError(errorText(err))
      setBusy(false)
    }
  }

  return (
    <main className="login">
      <form className="card" onSubmit={submit}>
        <div className="logo">Apolo</div>
        <h1>Configurar empresa</h1>
        <p className="muted flush">Primera vez: da de alta la empresa y tu usuario administrador. Los datos fiscales se capturan después en Configuración.</p>
        <label>Token de instalación<input name="token" type="password" required autoFocus autoComplete="off" /></label>
        <label>Nombre de la empresa<input name="company" required /></label>
        <label>Tu nombre<input name="name" required autoComplete="name" /></label>
        <label>Correo<input name="email" type="email" required autoComplete="username" /></label>
        <label>Contraseña<input name="password" type="password" required minLength={12} autoComplete="new-password" /></label>
        <label>Confirmar contraseña<input name="confirm" type="password" required minLength={12} autoComplete="new-password" /></label>
        <p className="muted flush">Mínimo 12 caracteres con letras y números.</p>
        {error && <p className="error" role="alert">{error}</p>}
        <button className="btn" disabled={busy}>{busy ? 'Creando…' : 'Crear empresa'}</button>
      </form>
    </main>
  )
}

function Shell({ me, onLogout }: { me: Me; onLogout: () => void }) {
  const { pathname } = useLocation()
  const current = me.modules.find(m => pathname.startsWith(`/${m.code}`))
  const dark = document.documentElement.dataset.theme === 'dark'
  const [, rerender] = useState(0)

  function toggleTheme() {
    const next = dark ? 'light' : 'dark'
    applyTheme(next)
    rerender(n => n + 1)
    api('/me/theme', { method: 'PUT', body: { theme: next } }).catch(() => {})
  }

  return (
    <div className="app">
      <header className="topbar">
        <div className="logo">Apolo</div>
        <div className="title">{current?.name}</div>
        <button className="icon-btn" onClick={toggleTheme} aria-label={dark ? 'Modo claro' : 'Modo oscuro'}>
          {dark ? '☀' : '☾'}
        </button>
        <div className="user">
          <div>{me.name}</div>
          <div className="muted">{me.role} · {me.company.name}</div>
        </div>
        <button className="btn secondary" onClick={onLogout}>Salir</button>
      </header>

      <nav className="sidebar" aria-label="Módulos">
        {GROUPS.map(g => {
          const items = me.modules.filter(m => m.group === g)
          if (!items.length) return null
          return (
            <div key={g}>
              <div className="group">{g}</div>
              {items.map(m => (
                <NavLink key={m.code} to={`/${m.code}`} className="nav-link">{m.name}</NavLink>
              ))}
            </div>
          )
        })}
      </nav>

      <main className="main">
        <Routes>
          {me.modules.map(m => (
            <Route key={m.code} path={`/${m.code}/*`}
              element={m.code === 'configuracion' ? <Configuracion />
                : m.code === 'inventario' ? <Inventario currency={me.company.currency} />
                : m.code === 'ventas' ? <Ventas currency={me.company.currency} />
                : m.code === 'facturacion' ? <Facturacion currency={me.company.currency} />
                : m.code === 'compras' ? <Compras currency={me.company.currency} />
                : m.code === 'proyectos' ? <Proyectos currency={me.company.currency} />
                : m.code === 'ia' ? <Ia />
                : m.code === 'contabilidad' ? <Contabilidad currency={me.company.currency} />
                : m.code === 'rrhh' ? <Rrhh currency={me.company.currency} />
                : m.code === 'reportes' ? <Reportes currency={me.company.currency} />
                : m.code === 'dashboard' ? <Dashboard me={me} />
                : <Pending module={m} />} />
          ))}
          <Route path="*" element={me.modules.length
            ? <Navigate to={`/${me.modules[0].code}`} replace />
            : <p className="muted">Tu rol no tiene módulos asignados.</p>} />
        </Routes>
      </main>
      {me.modules.some(m => m.code === 'ia') && current?.code !== 'ia' && <Copilot />}
    </div>
  )
}

function Pending({ module }: { module: Module }) {
  return (
    <>
      <div className="page-head"><div><h1>{module.name}</h1></div></div>
      <div className="card">
        <p className="section-title">En construcción</p>
        <p className="muted">Este módulo llega en la fase {PHASE[module.code]} del plan.</p>
      </div>
    </>
  )
}
