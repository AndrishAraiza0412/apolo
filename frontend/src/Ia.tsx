import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { api, errorText } from './api'
import Jobs from './Tareas'

type Turn = { role: 'user' | 'assistant'; text: string; consulted?: { tool: string; ok: boolean }[] }
type Tool = { name: string; description: string; writes: boolean }
type Action = {
  id: string; kind: string; summary: string; status: string; createdAt: string; decidedAt: string | null
  resultId: string | null; error: string | null; requestedBy: string | null; decidedBy: string | null
}

const TOOL_NAMES: Record<string, string> = {
  consultar_ventas: 'Ventas facturadas',
  consultar_cartera: 'Cartera por cobrar',
  consultar_pipeline: 'Pipeline de ventas',
  consultar_stock: 'Existencias',
  crear_factura_borrador: 'Proponer factura',
  crear_oc_borrador: 'Proponer orden de compra',
}
const KIND: Record<string, { label: string; to: string }> = {
  factura: { label: 'Factura', to: '/facturacion' },
  orden_compra: { label: 'Orden de compra', to: '/compras' },
}
const STATUS: Record<string, string> = { pendiente: '', aprobada: 'ok', rechazada: 'muted-pill' }
const when = new Intl.DateTimeFormat('es-MX', { dateStyle: 'medium', timeStyle: 'short' })

// La API espera que la conversación empiece con el usuario.
// ponytail: solo los últimos 20 turnos; resumir el historial si las conversaciones se alargan.
function trim(turns: Turn[]) {
  const recent = turns.slice(-20)
  while (recent[0]?.role === 'assistant') recent.shift()
  return recent
}

// La conversación vive mientras la pestaña está abierta y se comparte entre el panel y la pantalla completa.
let history: Turn[] = []

function useTools() {
  const [state, setState] = useState<{ configured: boolean; tools: Tool[] } | null>(null)
  useEffect(() => { api<{ configured: boolean; tools: Tool[] }>('/ia/tools').then(setState).catch(() => {}) }, [])
  return state
}

export function Chat({ compact = false, onConsulted }: { compact?: boolean; onConsulted?: (c: Turn['consulted']) => void }) {
  const [turns, setTurns] = useState<Turn[]>(history)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const end = useRef<HTMLDivElement>(null)

  useEffect(() => { history = turns; end.current?.scrollIntoView({ block: 'end' }) }, [turns])

  async function send(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const form = e.currentTarget
    const text = String(new FormData(form).get('q') ?? '').trim()
    if (!text || busy) return
    const next: Turn[] = [...turns, { role: 'user', text }]
    setTurns(next); setBusy(true); setError(''); form.reset()
    try {
      const r = await api<{ reply: string; consulted: Turn['consulted'] }>('/ia/chat', {
        method: 'POST', body: { messages: trim(next).map(({ role, text }) => ({ role, text })) },
      })
      setTurns([...next, { role: 'assistant', text: r.reply || '(sin respuesta)', consulted: r.consulted }])
      onConsulted?.(r.consulted)
    } catch (err) {
      setError(errorText(err))
      setTurns(turns)
    }
    setBusy(false)
  }

  return (
    <div className={`chat ${compact ? 'compact' : ''}`}>
      <div className="chat-log" aria-live="polite">
        {!turns.length && (
          <p className="muted">Pregunta por ventas, cartera, existencias o pide un borrador de factura u orden de compra. Todo borrador espera tu aprobación.</p>
        )}
        {turns.map((t, i) => (
          <div key={i} className={`bubble ${t.role}`}>
            <div className="bubble-text">{t.text}</div>
            {!!t.consulted?.length && (
              <div className="chips">
                {t.consulted.map((c, j) => <span key={j} className={`pill ${c.ok ? '' : 'bad'}`}>{TOOL_NAMES[c.tool] ?? c.tool}</span>)}
              </div>
            )}
          </div>
        ))}
        {busy && <div className="bubble assistant typing" aria-label="Apolo IA está escribiendo"><span /><span /><span /></div>}
        <div ref={end} />
      </div>
      {error && <p className="error" role="alert">{error}</p>}
      <form onSubmit={send} className="chat-form">
        <input name="q" placeholder="Escribe tu pregunta…" autoComplete="off" disabled={busy} aria-label="Pregunta para Apolo IA" />
        <button className="btn" disabled={busy}>{busy ? 'Pensando…' : 'Enviar'}</button>
        {!!turns.length && <button type="button" className="btn secondary" disabled={busy} onClick={() => setTurns([])}>Nueva</button>}
      </form>
    </div>
  )
}

export function Queue({ onChanged }: { onChanged?: () => void }) {
  const [actions, setActions] = useState<Action[]>([])
  const [busy, setBusy] = useState('')
  const [error, setError] = useState('')
  const load = () => api<Action[]>('/ia/actions').then(setActions)
  useEffect(() => { load() }, [])

  async function decide(a: Action, decision: 'approve' | 'reject') {
    setBusy(a.id + decision); setError('')
    try { await api(`/ia/actions/${a.id}/${decision}`, { method: 'POST' }); onChanged?.() } catch (err) { setError(errorText(err)) }
    await load()
    setBusy('')
  }

  return (
    <div className="stack">
      {error && <p className="error" role="alert">{error}</p>}
      {!actions.length && <div className="card empty">No hay propuestas del agente. Las propuestas que requieren tu aprobación aparecerán aquí.</div>}
      {actions.map(a => (
        <div key={a.id} className="card agent-task">
          <div className="detail-head">
            <div>
              <div className="muted">{KIND[a.kind].label} · pedida por {a.requestedBy ?? '—'} · {when.format(new Date(a.createdAt))}</div>
              <div className="agent-summary">{a.summary}</div>
              {a.decidedAt && <div className="muted">{a.status === 'aprobada' ? 'Aprobada' : 'Rechazada'} por {a.decidedBy ?? '—'} · {when.format(new Date(a.decidedAt))}</div>}
            </div>
            <span className={`pill ${STATUS[a.status]}`}>{a.status[0].toUpperCase() + a.status.slice(1)}</span>
          </div>
          {a.status === 'pendiente' ? (
            <div className="actions">
              <button className="btn" disabled={!!busy} onClick={() => decide(a, 'approve')}>
                {busy === a.id + 'approve' ? 'Creando borrador…' : 'Aprobar y crear borrador'}
              </button>
              <button className="btn secondary" disabled={!!busy} onClick={() => decide(a, 'reject')}>Rechazar</button>
            </div>
          ) : a.resultId && (
            <div className="actions"><Link className="btn secondary" to={KIND[a.kind].to}>Abrir en {KIND[a.kind].to === '/compras' ? 'Compras' : 'Facturación'}</Link></div>
          )}
        </div>
      ))}
    </div>
  )
}

export default function Ia() {
  const [tab, setTab] = useState<'chat' | 'cola'>('chat')
  const tools = useTools()
  const [last, setLast] = useState<Turn['consulted']>([])

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Apolo IA</h1>
          <div className="muted">Consulta los datos de tu rol y prepara borradores que tú apruebas.</div>
        </div>
      </div>
      <div className="tabs" role="tablist">
        <button role="tab" aria-selected={tab === 'chat'} onClick={() => setTab('chat')}>Conversación</button>
        <button role="tab" aria-selected={tab === 'cola'} onClick={() => setTab('cola')}>Cola de tareas</button>
      </div>
      {tab === 'cola' ? (
        <div className="stack">
          <section><p className="section-title">Propuestas por aprobar</p><Queue /></section>
          <section className="card"><p className="section-title">Tareas en segundo plano</p><Jobs /></section>
        </div>
      ) : (
        <div className="split">
          <div className="card">
            {tools && !tools.configured
              ? <p className="error">Apolo IA no está configurado en el servidor: falta la variable ANTHROPIC_API_KEY.</p>
              : <Chat onConsulted={setLast} />}
          </div>
          <div className="stack">
            <div className="card">
              <p className="section-title">Datos consultados</p>
              {last?.length
                ? <ul className="plain">{last.map((c, i) => <li key={i}>{c.ok ? '✓' : '✗'} {TOOL_NAMES[c.tool] ?? c.tool}</li>)}</ul>
                : <p className="muted">La última respuesta no consultó datos.</p>}
            </div>
            <div className="card">
              <p className="section-title">Permisos de tu rol</p>
              <ul className="plain">
                {tools?.tools.map(t => (
                  <li key={t.name}><strong>{TOOL_NAMES[t.name] ?? t.name}</strong>{t.writes && <span className="muted"> · requiere aprobación</span>}</li>
                ))}
                {tools && !tools.tools.length && <li className="muted">Tu rol no tiene herramientas de datos.</li>}
              </ul>
            </div>
          </div>
        </div>
      )}
    </>
  )
}

/// Botón flotante y panel lateral, disponibles desde cualquier módulo.
export function Copilot() {
  const [open, setOpen] = useState(false)
  const tools = useTools()
  useEffect(() => {
    if (!open) return
    const close = (e: KeyboardEvent) => e.key === 'Escape' && setOpen(false)
    window.addEventListener('keydown', close)
    return () => window.removeEventListener('keydown', close)
  }, [open])

  return (
    <>
      <button className="copilot-fab" onClick={() => setOpen(o => !o)} aria-expanded={open}>Preguntar a la IA</button>
      {open && (
        <aside className="drawer" aria-label="Apolo IA">
          <div className="detail-head">
            <p className="section-title flush">Apolo IA</p>
            <button className="icon-btn" onClick={() => setOpen(false)} aria-label="Cerrar panel">×</button>
          </div>
          {tools && !tools.configured
            ? <p className="error">Apolo IA no está configurado en el servidor: falta la variable ANTHROPIC_API_KEY.</p>
            : <Chat compact />}
        </aside>
      )}
    </>
  )
}
