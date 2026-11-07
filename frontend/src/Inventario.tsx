import { useEffect, useState, type FormEvent } from 'react'
import { api, errorText } from './api'
import { barPath, niceStep } from './chart'

type Product = {
  id: string; sku: string; name: string; unit: string; satCode: string; satUnit: string
  cost: number; price: number; minStock: number; active: boolean; stock: number
}
type Movement = { id: string; createdAt: string; kind: string; quantity: number; note: string | null; user: string | null }

const qty = new Intl.NumberFormat('es-MX', { maximumFractionDigits: 4 })
const date = new Intl.DateTimeFormat('es-MX', { dateStyle: 'medium', timeStyle: 'short' })

export default function Inventario({ currency }: { currency: string }) {
  const money = new Intl.NumberFormat('es-MX', { style: 'currency', currency })
  const [products, setProducts] = useState<Product[]>([])
  const [q, setQ] = useState('')
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [creating, setCreating] = useState(false)

  const load = () => api<Product[]>(`/products?q=${encodeURIComponent(q)}`).then(setProducts)
  useEffect(() => { load() }, [q]) // eslint-disable-line react-hooks/exhaustive-deps

  const selected = products.find(p => p.id === selectedId)
  const low = products.filter(p => p.active && p.stock < p.minStock).length

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Inventario</h1>
          <div className="muted">{products.length} productos · {low} bajo mínimo</div>
        </div>
        <input type="search" placeholder="Buscar SKU o nombre" value={q} onChange={e => setQ(e.target.value)} aria-label="Buscar" />
        <button className="btn" onClick={() => setCreating(c => !c)}>{creating ? 'Cancelar' : 'Nuevo producto'}</button>
      </div>

      {creating && (
        <div className="card gap-below">
          <p className="section-title">Nuevo producto</p>
          <ProductForm onSaved={id => { setCreating(false); setSelectedId(id); load() }} />
        </div>
      )}

      <div className={selected ? 'split' : ''}>
        <div className="card">
          <table>
            <thead>
              <tr><th>SKU</th><th>Producto</th><th className="num">Existencia</th><th className="num">Mínimo</th><th className="num">Precio</th><th>Estado</th></tr>
            </thead>
            <tbody>
              {products.map(p => (
                <tr key={p.id} tabIndex={0} aria-selected={p.id === selectedId} className="clickable"
                  onClick={() => setSelectedId(p.id)} onKeyDown={e => e.key === 'Enter' && setSelectedId(p.id)}>
                  <td>{p.sku}</td>
                  <td>{p.name}</td>
                  <td className="num">{qty.format(p.stock)} <span className="muted">{p.unit}</span></td>
                  <td className="num">{qty.format(p.minStock)}</td>
                  <td className="num">{money.format(p.price)}</td>
                  <td><Status p={p} /></td>
                </tr>
              ))}
              {!products.length && <tr><td colSpan={6} className="empty">Sin productos. Da de alta el primero con «Nuevo producto».</td></tr>}
            </tbody>
          </table>
        </div>

        {selected && <Detail key={selected.id} p={selected} money={money} onChanged={load} onClose={() => setSelectedId(null)} />}
      </div>
    </>
  )
}

function Status({ p }: { p: Product }) {
  if (!p.active) return <span className="pill">Inactivo</span>
  return p.stock < p.minStock ? <span className="pill bad">Bajo mínimo</span> : <span className="pill ok">OK</span>
}

function ProductForm({ p, onSaved }: { p?: Product; onSaved: (id: string) => void }) {
  const [error, setError] = useState('')

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const f = Object.fromEntries(new FormData(e.currentTarget))
    const body = { ...f, cost: Number(f.cost), price: Number(f.price), minStock: Number(f.minStock), active: f.active !== undefined || !p }
    setError('')
    try {
      if (p) {
        await api(`/products/${p.id}`, { method: 'PUT', body })
        onSaved(p.id)
      } else {
        onSaved((await api<{ id: string }>('/products', { method: 'POST', body })).id)
      }
    } catch (err) { setError(errorText(err)) }
  }

  return (
    <form onSubmit={submit} className="form-grid">
      <label>SKU<input name="sku" defaultValue={p?.sku} required /></label>
      <label className="wide">Nombre<input name="name" defaultValue={p?.name} required /></label>
      <label>Unidad<input name="unit" defaultValue={p?.unit ?? 'pza'} /></label>
      <label>Clave SAT<input name="satCode" defaultValue={p?.satCode ?? '01010101'} inputMode="numeric" pattern="\d{8}" maxLength={8} title="Clave de producto o servicio del SAT, 8 dígitos" /></label>
      <label>Unidad SAT<input name="satUnit" defaultValue={p?.satUnit ?? 'H87'} maxLength={3} title="Clave de unidad del SAT: H87 pieza, KGM kilogramo, MTR metro, E48 servicio" /></label>
      <label>Costo<input name="cost" type="number" min="0" step="0.01" defaultValue={p?.cost ?? 0} /></label>
      <label>Precio<input name="price" type="number" min="0" step="0.01" defaultValue={p?.price ?? 0} /></label>
      <label>Mínimo<input name="minStock" type="number" min="0" step="any" defaultValue={p?.minStock ?? 0} /></label>
      {p && <label className="check"><input name="active" type="checkbox" defaultChecked={p.active} /> Activo</label>}
      <button className="btn">{p ? 'Guardar' : 'Crear'}</button>
      {error && <p className="error wide" role="alert">{error}</p>}
    </form>
  )
}

function Detail({ p, money, onChanged, onClose }: { p: Product; money: Intl.NumberFormat; onChanged: () => void; onClose: () => void }) {
  const [movements, setMovements] = useState<Movement[]>([])
  const [editing, setEditing] = useState(false)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  const load = () => api<Movement[]>(`/products/${p.id}/movements`).then(setMovements)
  useEffect(() => { load() }, []) // eslint-disable-line react-hooks/exhaustive-deps

  async function register(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const form = e.currentTarget
    const f = Object.fromEntries(new FormData(form))
    setError('')
    setBusy(true)
    try {
      await api(`/products/${p.id}/movements`, { method: 'POST', body: { ...f, quantity: Number(f.quantity) } })
      form.reset()
      load()
      onChanged()
    } catch (err) { setError(errorText(err)) }
    setBusy(false)
  }

  return (
    <aside className="card stack detail" aria-label={`Detalle de ${p.sku}`}>
      <div className="detail-head">
        <div>
          <div className="muted">{p.sku}</div>
          <strong>{p.name}</strong>
        </div>
        <button className="icon-btn" onClick={onClose} aria-label="Cerrar detalle">×</button>
      </div>

      <div className="kpis">
        <div><div className="muted">Existencia</div><div className="kpi">{qty.format(p.stock)}</div><div className="muted">{p.unit}</div></div>
        <div><div className="muted">Valor a costo</div><div className="kpi small">{money.format(p.stock * p.cost)}</div></div>
      </div>

      <Forecast key={p.stock} p={p} />

      {editing
        ? <ProductForm p={p} onSaved={() => { setEditing(false); onChanged() }} />
        : <button className="btn secondary" onClick={() => setEditing(true)}>Editar producto</button>}

      <form onSubmit={register} className="stack">
        <p className="section-title flush">Registrar movimiento</p>
        <div className="form-grid">
          <label>Tipo
            <select name="kind" defaultValue="entrada">
              <option value="entrada">Entrada</option>
              <option value="salida">Salida</option>
              <option value="ajuste">Ajuste (±)</option>
            </select>
          </label>
          <label>Cantidad<input name="quantity" type="number" step="any" required /></label>
          <label className="wide">Nota<input name="note" placeholder="Opcional" /></label>
        </div>
        <button className="btn" disabled={busy}>{busy ? 'Registrando…' : 'Registrar'}</button>
        {error && <p className="error" role="alert">{error}</p>}
      </form>

      <div>
        <p className="section-title">Movimientos</p>
        <table>
          <tbody>
            {movements.map(m => (
              <tr key={m.id}>
                <td>
                  <div>{m.kind}{m.note && <span className="muted"> · {m.note}</span>}</div>
                  <div className="muted">{date.format(new Date(m.createdAt))} · {m.user ?? 'Sistema'}</div>
                </td>
                <td className="num">{m.quantity > 0 ? '+' : ''}{qty.format(m.quantity)}</td>
              </tr>
            ))}
            {!movements.length && <tr><td className="empty">Sin movimientos.</td></tr>}
          </tbody>
        </table>
      </div>
    </aside>
  )
}

type Demand = {
  dailyDemand: number; daysToMinimum: number | null; daysToStockout: number | null; suggestedOrder: number
  days: { date: string; stock: number }[]
}
const day = new Intl.DateTimeFormat('es-MX', { day: 'numeric', month: 'short', timeZone: 'UTC' })

// Existencia proyectada 14 días con la demanda pronosticada; el mínimo es la línea punteada.
function Forecast({ p }: { p: Product }) {
  const [d, setD] = useState<Demand | null>(null)
  const [hover, setHover] = useState<number | null>(null)
  useEffect(() => { api<Demand>(`/forecast/demand/${p.id}`).then(setD).catch(() => setD(null)) }, [p.id])
  if (!d) return null
  if (!d.dailyDemand) return <p className="muted">Sin salidas en los últimos 90 días: no hay pronóstico de demanda.</p>

  const W = 320, H = 130, L = 34, B = 18, T = 10
  const max = Math.max(p.stock, p.minStock, 1), step = niceStep(max / 2)
  const top = Math.ceil(max / step) * step
  const y = (v: number) => H - B - (Math.max(v, 0) / top) * (H - B - T)
  const slot = (W - L) / d.days.length
  const bw = slot - 2

  return (
    <div className="forecast">
      <p className="section-title flush">Pronóstico · próximos 14 días</p>
      <div className="chart">
        <svg viewBox={`0 0 ${W} ${H}`} role="img" aria-label={`Existencia proyectada de ${p.sku} para 14 días`} onMouseLeave={() => setHover(null)}>
          {[0, top].map(t => <text key={t} x={L - 6} y={y(t)} className="axis" textAnchor="end" dominantBaseline="middle">{qty.format(t)}</text>)}
          <line x1={L} x2={W} y1={y(0)} y2={y(0)} className="grid" />
          {d.days.map((x, i) => {
            const h = H - B - y(x.stock)
            return (
              <g key={x.date} onMouseEnter={() => setHover(i)}>
                <rect x={L + slot * i} y={T} width={slot} height={H - B - T} fill="transparent" />
                {h > 0 && <path d={barPath(L + slot * i + 1, y(x.stock), bw, h)} className={`bar ${x.stock < p.minStock ? 'below' : ''} ${hover === i ? 'hot' : ''}`} style={{ animationDelay: `${i * 35}ms` }} />}
              </g>
            )
          })}
          {p.minStock > 0 && <>
            <line x1={L} x2={W} y1={y(p.minStock)} y2={y(p.minStock)} className="min-line" />
            <text x={W} y={y(p.minStock) - 4} className="min-label" textAnchor="end">Mínimo {qty.format(p.minStock)}</text>
          </>}
          <text x={L} y={H - 4} className="axis">{day.format(new Date(d.days[0].date))}</text>
          <text x={W} y={H - 4} className="axis" textAnchor="end">{day.format(new Date(d.days[d.days.length - 1].date))}</text>
        </svg>
        {hover !== null && (
          <div className="tooltip" style={{ left: `${((L + slot * hover + slot / 2) / W) * 100}%`, top: `${(y(d.days[hover].stock) / H) * 100}%` }}>
            <strong>{qty.format(d.days[hover].stock)} {p.unit}</strong><span className="muted"> · {day.format(new Date(d.days[hover].date))}</span>
          </div>
        )}
      </div>
      <div className="forecast-facts">
        <div><span className="muted">Demanda diaria</span><strong>{qty.format(d.dailyDemand)}</strong></div>
        <div><span className="muted">Llega al mínimo</span><strong>{d.daysToMinimum === 0 ? 'Ya' : d.daysToMinimum === null ? '—' : `${d.daysToMinimum} d`}</strong></div>
        <div><span className="muted">Sugerido pedir</span><strong>{qty.format(d.suggestedOrder)}</strong></div>
      </div>
      <details className="data-table">
        <summary>Ver datos</summary>
        <table><tbody>{d.days.map(x => <tr key={x.date}><td>{day.format(new Date(x.date))}</td><td className="num">{qty.format(x.stock)}</td></tr>)}</tbody></table>
      </details>
    </div>
  )
}
