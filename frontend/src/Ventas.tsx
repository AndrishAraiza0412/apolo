import { useEffect, useState, type FormEvent } from 'react'
import { api, errorText } from './api'
import { useSat } from './sat'

export type Customer = {
  id: string; name: string; rfc: string | null; email: string | null; phone: string | null
  creditDays: number; active: boolean; openAmount: number
  taxRegime?: string | null; zipCode?: string | null; cfdiUse?: string
}
type Opportunity = {
  id: string; customerId: string; customer: string; title: string; amount: number; stage: string
  expectedClose: string | null; ownerId: string | null; owner: string | null
}
type Seller = { id: string; name: string }

const STAGES = [
  { code: 'prospecto', name: 'Prospecto' },
  { code: 'propuesta', name: 'Propuesta' },
  { code: 'negociacion', name: 'Negociación' },
  { code: 'ganada', name: 'Ganada' },
  { code: 'perdida', name: 'Perdida' },
]
const day = new Intl.DateTimeFormat('es-MX', { day: 'numeric', month: 'short', timeZone: 'UTC' })

export default function Ventas({ currency }: { currency: string }) {
  const money = new Intl.NumberFormat('es-MX', { style: 'currency', currency, maximumFractionDigits: 0 })
  const [tab, setTab] = useState<'oportunidades' | 'clientes'>('oportunidades')
  const [customers, setCustomers] = useState<Customer[]>([])
  const loadCustomers = () => api<Customer[]>('/customers').then(setCustomers)
  useEffect(() => { loadCustomers() }, [])

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Ventas / CRM</h1>
          <div className="muted">Clientes y oportunidades de venta.</div>
        </div>
      </div>
      <div className="tabs" role="tablist">
        <button role="tab" aria-selected={tab === 'oportunidades'} onClick={() => setTab('oportunidades')}>Oportunidades</button>
        <button role="tab" aria-selected={tab === 'clientes'} onClick={() => setTab('clientes')}>Clientes</button>
      </div>
      {tab === 'oportunidades'
        ? <Pipeline customers={customers.filter(c => c.active)} money={money} />
        : <Clientes customers={customers} money={money} onChanged={loadCustomers} />}
    </>
  )
}

function Pipeline({ customers, money }: { customers: Customer[]; money: Intl.NumberFormat }) {
  const [opps, setOpps] = useState<Opportunity[]>([])
  const [sellers, setSellers] = useState<Seller[]>([])
  const [editing, setEditing] = useState<Opportunity | 'new' | null>(null)
  const [error, setError] = useState('')

  const load = () => api<Opportunity[]>('/opportunities').then(setOpps)
  useEffect(() => { load(); api<Seller[]>('/sellers').then(setSellers) }, [])

  const open = opps.filter(o => o.stage !== 'ganada' && o.stage !== 'perdida')
  const won = opps.filter(o => o.stage === 'ganada')
  const sum = (xs: Opportunity[]) => xs.reduce((s, o) => s + o.amount, 0)

  async function move(o: Opportunity, stage: string) {
    setError('')
    try {
      await api(`/opportunities/${o.id}`, { method: 'PUT', body: { ...o, stage } })
      load()
    } catch (err) { setError(errorText(err)) }
  }

  return (
    <div className="stack">
      <div className="kpi-row">
        <div className="card"><div className="muted">Pipeline abierto</div><div className="kpi">{money.format(sum(open))}</div><div className="muted">{open.length} oportunidades</div></div>
        <div className="card"><div className="muted">Ganado</div><div className="kpi">{money.format(sum(won))}</div><div className="muted">{won.length} oportunidades</div></div>
        <div className="card kpi-action">
          <button className="btn" onClick={() => setEditing(editing ? null : 'new')}>{editing ? 'Cancelar' : 'Nueva oportunidad'}</button>
        </div>
      </div>

      {editing && (
        <div className="card">
          <p className="section-title">{editing === 'new' ? 'Nueva oportunidad' : 'Editar oportunidad'}</p>
          <OpportunityForm key={editing === 'new' ? 'new' : editing.id} o={editing === 'new' ? undefined : editing}
            customers={customers} sellers={sellers} onSaved={() => { setEditing(null); load() }} />
        </div>
      )}
      {error && <p className="error" role="alert">{error}</p>}

      <div className="board">
        {STAGES.map(s => {
          const items = opps.filter(o => o.stage === s.code)
          return (
            <section key={s.code} className="column" aria-label={s.name}>
              <header>
                <span className="section-title flush">{s.name}</span>
                <span className="muted">{money.format(sum(items))}</span>
              </header>
              {items.map(o => (
                <article key={o.id} className="card opp">
                  <button className="link" onClick={() => setEditing(o)}>{o.title}</button>
                  <div className="muted">{o.customer}</div>
                  <div className="opp-amount">{money.format(o.amount)}</div>
                  <div className="muted">
                    {o.expectedClose && <>Cierre {day.format(new Date(o.expectedClose))}</>}
                    {o.owner && <> · {o.owner}</>}
                  </div>
                  <select value={o.stage} onChange={e => move(o, e.target.value)} aria-label={`Etapa de ${o.title}`}>
                    {STAGES.map(x => <option key={x.code} value={x.code}>{x.name}</option>)}
                  </select>
                </article>
              ))}
              {!items.length && <p className="muted">Sin oportunidades.</p>}
            </section>
          )
        })}
      </div>
    </div>
  )
}

function OpportunityForm({ o, customers, sellers, onSaved }: {
  o?: Opportunity; customers: Customer[]; sellers: Seller[]; onSaved: () => void
}) {
  const [error, setError] = useState('')

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const f = Object.fromEntries(new FormData(e.currentTarget)) as Record<string, string>
    const body = {
      customerId: f.customerId, title: f.title, amount: Number(f.amount), stage: f.stage,
      expectedClose: f.expectedClose || null, ownerId: f.ownerId || null,
    }
    setError('')
    try {
      await api(o ? `/opportunities/${o.id}` : '/opportunities', { method: o ? 'PUT' : 'POST', body })
      onSaved()
    } catch (err) { setError(errorText(err)) }
  }

  return (
    <form onSubmit={submit} className="form-grid">
      <label className="wide">Título<input name="title" defaultValue={o?.title} required /></label>
      <label>Cliente
        <select name="customerId" defaultValue={o?.customerId} required>
          {customers.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
        </select>
      </label>
      <label>Monto<input name="amount" type="number" min="0" step="0.01" defaultValue={o?.amount ?? 0} required /></label>
      <label>Etapa
        <select name="stage" defaultValue={o?.stage ?? 'prospecto'}>
          {STAGES.map(s => <option key={s.code} value={s.code}>{s.name}</option>)}
        </select>
      </label>
      <label>Cierre estimado<input name="expectedClose" type="date" defaultValue={o?.expectedClose ?? ''} /></label>
      <label>Responsable
        <select name="ownerId" defaultValue={o?.ownerId ?? ''}>
          <option value="">Sin asignar</option>
          {sellers.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}
        </select>
      </label>
      <button className="btn">{o ? 'Guardar' : 'Crear'}</button>
      {error && <p className="error wide" role="alert">{error}</p>}
    </form>
  )
}

// Clientes y proveedores comparten pantalla; Compras la usa con kind="proveedor".
const KIND = {
  cliente: { path: '/customers', plural: 'clientes', amount: 'Pipeline abierto' },
  proveedor: { path: '/suppliers', plural: 'proveedores', amount: 'Por recibir' },
}

export function Clientes({ customers, money, onChanged, kind = 'cliente' }: {
  customers: Customer[]; money: Intl.NumberFormat; onChanged: () => void; kind?: keyof typeof KIND
}) {
  const k = KIND[kind]
  const [editing, setEditing] = useState<Customer | 'new' | null>(null)
  const [q, setQ] = useState('')
  const term = q.trim().toLowerCase()
  const shown = term ? customers.filter(c => c.name.toLowerCase().includes(term) || c.rfc?.toLowerCase().includes(term)) : customers

  return (
    <div className="stack">
      <div className="page-head flush">
        <div className="muted">{customers.length} {k.plural}</div>
        <input type="search" placeholder="Buscar nombre o RFC" value={q} onChange={e => setQ(e.target.value)} aria-label={`Buscar ${kind}`} />
        <button className="btn" onClick={() => setEditing(editing ? null : 'new')}>{editing ? 'Cancelar' : `Nuevo ${kind}`}</button>
      </div>

      {editing && (
        <div className="card">
          <p className="section-title">{editing === 'new' ? `Nuevo ${kind}` : `Editar ${kind}`}</p>
          <CustomerForm path={k.path} key={editing === 'new' ? 'new' : editing.id} c={editing === 'new' ? undefined : editing}
            onSaved={() => { setEditing(null); onChanged() }} />
        </div>
      )}

      <div className="card">
        <table>
          <thead>
            <tr><th>{kind === 'cliente' ? 'Cliente' : 'Proveedor'}</th><th>RFC</th><th>Contacto</th><th className="num">Crédito</th><th className="num">{k.amount}</th><th>Estado</th></tr>
          </thead>
          <tbody>
            {shown.map(c => (
              <tr key={c.id} tabIndex={0} className="clickable"
                aria-selected={editing !== 'new' && editing?.id === c.id}
                onClick={() => setEditing(c)} onKeyDown={e => e.key === 'Enter' && setEditing(c)}>
                <td>{c.name}</td>
                <td>{c.rfc ?? <span className="muted">—</span>}</td>
                <td>{c.email ?? c.phone ?? <span className="muted">—</span>}</td>
                <td className="num">{c.creditDays ? `${c.creditDays} días` : 'Contado'}</td>
                <td className="num">{money.format(c.openAmount)}</td>
                <td><span className={`pill ${c.active ? 'ok' : ''}`}>{c.active ? 'Activo' : 'Inactivo'}</span></td>
              </tr>
            ))}
            {!shown.length && <tr><td colSpan={6} className="empty">{term ? `Ningún resultado para «${term}».` : `Sin ${k.plural} todavía.`}</td></tr>}
          </tbody>
        </table>
      </div>
    </div>
  )
}

function CustomerForm({ c, path, onSaved }: { c?: Customer; path: string; onSaved: () => void }) {
  const [error, setError] = useState('')
  const sat = useSat()

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const f = Object.fromEntries(new FormData(e.currentTarget))
    const body = { ...f, creditDays: Number(f.creditDays), active: f.active !== undefined || !c }
    setError('')
    try {
      await api(c ? `${path}/${c.id}` : path, { method: c ? 'PUT' : 'POST', body })
      onSaved()
    } catch (err) { setError(errorText(err)) }
  }

  return (
    <form onSubmit={submit} className="form-grid">
      <label className="wide">Nombre o razón social<input name="name" defaultValue={c?.name} required /></label>
      <label>RFC<input name="rfc" defaultValue={c?.rfc ?? ''} maxLength={13} /></label>
      <label>Correo<input name="email" type="email" defaultValue={c?.email ?? ''} /></label>
      <label>Teléfono<input name="phone" type="tel" defaultValue={c?.phone ?? ''} /></label>
      <label>Días de crédito<input name="creditDays" type="number" min="0" max="365" defaultValue={c?.creditDays ?? 0} /></label>
      {path === '/customers' && sat && <>
        <label>Régimen fiscal
          <select name="taxRegime" defaultValue={c?.taxRegime ?? ''}>
            <option value="">Sin capturar</option>
            {Object.entries(sat.regimes).map(([k, v]) => <option key={k} value={k}>{k} · {v}</option>)}
          </select>
        </label>
        <label>C.P. fiscal<input name="zipCode" defaultValue={c?.zipCode ?? ''} inputMode="numeric" pattern="\d{5}" maxLength={5} /></label>
        <label>Uso de CFDI
          <select name="cfdiUse" defaultValue={c?.cfdiUse ?? 'G03'}>
            {Object.entries(sat.uses).map(([k, v]) => <option key={k} value={k}>{k} · {v}</option>)}
          </select>
        </label>
      </>}
      {c && <label className="check"><input name="active" type="checkbox" defaultChecked={c.active} /> Activo</label>}
      <button className="btn">{c ? 'Guardar' : 'Crear'}</button>
      {error && <p className="error wide" role="alert">{error}</p>}
    </form>
  )
}
