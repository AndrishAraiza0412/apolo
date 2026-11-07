import { useEffect, useState } from 'react'
import { api, errorText } from './api'
import { fmtDate, RATES, round, totals } from './totals'
import { Clientes, type Customer } from './Ventas'

type Row = { id: string; folioText: string; supplier: string; sentAt: string | null; receivedAt: string | null; total: number; balance: number; status: string }
type Line = { productId: string; quantity: number; unitCost: number; taxRate: number }
type Order = {
  id: string; folioText: string; supplierId: string; status: string; sentAt: string | null; receivedAt: string | null; paidAt: string | null
  notes: string | null; lines: Line[]; total: number; balance: number; payments: { date: string; amount: number }[]
}
type Catalog = {
  suppliers: { id: string; name: string }[]
  products: { id: string; sku: string; name: string; unit: string; cost: number; minStock: number; stock: number }[]
}

const STATUS: Record<string, { label: string; cls: string }> = {
  borrador: { label: 'Borrador', cls: '' },
  enviada: { label: 'Enviada', cls: '' },
  recibida: { label: 'Recibida', cls: '' },
  pagada: { label: 'Pagada', cls: 'ok' },
  cancelada: { label: 'Cancelada', cls: 'muted-pill' },
}

export default function Compras({ currency }: { currency: string }) {
  const money = new Intl.NumberFormat('es-MX', { style: 'currency', currency })
  const [tab, setTab] = useState<'ordenes' | 'proveedores'>('ordenes')
  const [rows, setRows] = useState<Row[]>([])
  const [suppliers, setSuppliers] = useState<Customer[]>([])
  const [catalog, setCatalog] = useState<Catalog>({ suppliers: [], products: [] })
  const [open, setOpen] = useState<string | 'new' | null>(null)

  const load = () => api<Row[]>('/purchases').then(setRows)
  const loadCatalog = () => api<Catalog>('/purchases/catalog').then(setCatalog)
  const loadSuppliers = () => api<Customer[]>('/suppliers').then(setSuppliers)
  useEffect(() => { load(); loadCatalog(); loadSuppliers() }, [])

  const pending = rows.filter(r => r.status === 'enviada')

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Compras</h1>
          <div className="muted">
            {pending.length} {pending.length === 1 ? 'orden' : 'órdenes'} por recibir · {money.format(pending.reduce((t, r) => t + r.total, 0))}
          </div>
        </div>
        {tab === 'ordenes' && (
          <button className="btn" onClick={() => setOpen(open === 'new' ? null : 'new')}>{open === 'new' ? 'Cancelar' : 'Nueva orden'}</button>
        )}
      </div>
      <div className="tabs" role="tablist">
        <button role="tab" aria-selected={tab === 'ordenes'} onClick={() => setTab('ordenes')}>Órdenes de compra</button>
        <button role="tab" aria-selected={tab === 'proveedores'} onClick={() => setTab('proveedores')}>Proveedores</button>
      </div>

      {tab === 'proveedores'
        ? <Clientes kind="proveedor" customers={suppliers} money={money} onChanged={() => { loadSuppliers(); loadCatalog() }} />
        : (
          <>
            {open && (
              <OrderEditor key={open} id={open === 'new' ? null : open} catalog={catalog} money={money}
                onClose={() => setOpen(null)}
                onChanged={id => { load(); loadCatalog(); loadSuppliers(); setOpen(id) }}
                onRefresh={() => { load(); loadCatalog() }} />
            )}
            <div className="card">
              <table>
                <thead>
                  <tr><th>Folio</th><th>Proveedor</th><th>Enviada</th><th>Recibida</th><th className="num">Total</th><th className="num">Saldo</th><th>Estado</th></tr>
                </thead>
                <tbody>
                  {rows.map(r => (
                    <tr key={r.id} tabIndex={0} className="clickable" aria-selected={open === r.id}
                      onClick={() => setOpen(r.id)} onKeyDown={e => e.key === 'Enter' && setOpen(r.id)}>
                      <td>{r.folioText}</td>
                      <td>{r.supplier}</td>
                      <td>{fmtDate(r.sentAt)}</td>
                      <td>{fmtDate(r.receivedAt)}</td>
                      <td className="num">{money.format(r.total)}</td>
                      <td className="num">{r.balance ? money.format(r.balance) : '—'}</td>
                      <td><span className={`pill ${STATUS[r.status].cls}`}>{STATUS[r.status].label}</span></td>
                    </tr>
                  ))}
                  {!rows.length && <tr><td colSpan={7} className="empty">Sin órdenes de compra. Crea una con «Nueva orden».</td></tr>}
                </tbody>
              </table>
            </div>
          </>
        )}
    </>
  )
}

function OrderEditor({ id, catalog, money, onClose, onChanged, onRefresh }: {
  id: string | null; catalog: Catalog; money: Intl.NumberFormat
  onClose: () => void; onChanged: (id: string) => void; onRefresh: () => void
}) {
  const [order, setOrder] = useState<Order | null>(null)
  const [supplierId, setSupplierId] = useState('')
  const [notes, setNotes] = useState('')
  const [lines, setLines] = useState<Line[]>([])
  const [error, setError] = useState('')
  const [busy, setBusy] = useState('')
  const [amount, setAmount] = useState('')
  // Igual que en Facturación: si el envío falla, el siguiente intento edita el borrador ya guardado.
  const [savedId, setSavedId] = useState(id)

  useEffect(() => {
    if (!id) return
    api<Order>(`/purchases/${id}`).then(o => { setOrder(o); setSupplierId(o.supplierId); setNotes(o.notes ?? ''); setLines(o.lines) })
  }, [id])

  const draft = !order || order.status === 'borrador'
  const t = totals(lines.map(l => [l.quantity, l.unitCost, l.taxRate]))
  const product = (pid: string) => catalog.products.find(p => p.id === pid)
  const set = (i: number, patch: Partial<Line>) => setLines(ls => ls.map((l, j) => (j === i ? { ...l, ...patch } : l)))
  const newLine = (p = catalog.products[0], quantity = 1): Line => ({ productId: p?.id ?? '', quantity, unitCost: p?.cost ?? 0, taxRate: 0.16 })

  // Con el pronóstico de demanda: lo que llega al mínimo en 14 días o menos, pidiendo para cubrir 30 días más el mínimo.
  async function suggest() {
    setError('')
    try {
      const demand = await api<{ productId: string; daysToMinimum: number | null; suggestedOrder: number }[]>('/forecast/demand')
      const picks = demand.filter(d => (d.daysToMinimum ?? Infinity) <= 14 && d.suggestedOrder > 0 && !lines.some(l => l.productId === d.productId))
      setLines(ls => [...ls, ...picks.flatMap(d => { const p = product(d.productId); return p ? [newLine(p, d.suggestedOrder)] : [] })])
      if (!picks.length) setError('El pronóstico no sugiere pedir nada más en los próximos 14 días.')
    } catch (err) { setError(errorText(err)) }
  }

  async function run(label: string, fn: () => Promise<string>) {
    setError(''); setBusy(label)
    try { onChanged(await fn()) } catch (err) { setError(errorText(err)); onRefresh() }
    setBusy('')
  }

  async function save() {
    const body = { supplierId: supplierId || catalog.suppliers[0]?.id, notes, lines }
    if (savedId) { await api(`/purchases/${savedId}`, { method: 'PUT', body }); return savedId }
    const created = (await api<{ id: string }>('/purchases', { method: 'POST', body })).id
    setSavedId(created)
    return created
  }

  const action = (path: string) => async () => { await api(`/purchases/${order!.id}/${path}`, { method: 'POST' }); return order!.id }
  const send = async () => { const saved = await save(); await api(`/purchases/${saved}/send`, { method: 'POST' }); return saved }

  if (id && !order) return null
  const lowCount = catalog.products.filter(p => p.stock < p.minStock).length

  return (
    <div className="card stack gap-below">
      <div className="detail-head">
        <div>
          <p className="section-title flush">{order ? `Orden ${order.folioText}` : 'Nueva orden de compra'}</p>
          {order?.sentAt && (
            <div className="muted">Enviada {fmtDate(order.sentAt)}{order.receivedAt && <> · recibida {fmtDate(order.receivedAt)}</>}{order.paidAt && <> · pagada {fmtDate(order.paidAt)}</>}</div>
          )}
        </div>
        {order && <span className={`pill ${STATUS[order.status].cls}`}>{STATUS[order.status].label}</span>}
        <button className="icon-btn" onClick={onClose} aria-label="Cerrar orden">×</button>
      </div>

      <div className="form-grid">
        <label>Proveedor
          <select value={supplierId || catalog.suppliers[0]?.id || ''} onChange={e => setSupplierId(e.target.value)} disabled={!draft}>
            {catalog.suppliers.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}
          </select>
        </label>
        <label className="wide">Notas<input value={notes} onChange={e => setNotes(e.target.value)} disabled={!draft} /></label>
      </div>

      <table className="lines">
        <thead>
          <tr><th>Producto</th><th className="num">Cantidad</th><th className="num">Costo unitario</th><th>IVA</th><th className="num">Importe</th>{draft && <th />}</tr>
        </thead>
        <tbody>
          {lines.map((l, i) => {
            const p = product(l.productId)
            return (
              <tr key={i}>
                <td>
                  <select value={l.productId} onChange={e => set(i, { productId: e.target.value, unitCost: product(e.target.value)?.cost ?? 0 })}
                    disabled={!draft} aria-label="Producto">
                    {catalog.products.map(x => <option key={x.id} value={x.id}>{x.sku} · {x.name}</option>)}
                  </select>
                  {draft && p && <div className={p.stock < p.minStock ? 'error' : 'muted'}>Existencia {p.stock} {p.unit} · mínimo {p.minStock}</div>}
                </td>
                <td><input className="num" type="number" min="0" step="any" value={l.quantity} onChange={e => set(i, { quantity: Number(e.target.value) })} disabled={!draft} aria-label="Cantidad" /></td>
                <td><input className="num" type="number" min="0" step="0.01" value={l.unitCost} onChange={e => set(i, { unitCost: Number(e.target.value) })} disabled={!draft} aria-label="Costo unitario" /></td>
                <td>
                  <select value={l.taxRate} onChange={e => set(i, { taxRate: Number(e.target.value) })} disabled={!draft} aria-label="IVA">
                    {RATES.map(r => <option key={r} value={r}>{r * 100}%</option>)}
                  </select>
                </td>
                <td className="num">{money.format(round(l.quantity * l.unitCost))}</td>
                {draft && <td><button className="icon-btn" onClick={() => setLines(ls => ls.filter((_, j) => j !== i))} aria-label="Quitar partida">×</button></td>}
              </tr>
            )
          })}
          {!lines.length && <tr><td colSpan={6} className="empty">Agrega partidas o usa la sugerencia por pronóstico.</td></tr>}
        </tbody>
      </table>

      <div className="invoice-foot">
        {draft ? (
          <div className="actions flush">
            <button className="btn secondary" onClick={() => setLines(ls => [...ls, newLine()])}>Agregar partida</button>
            <button className="btn secondary" onClick={suggest} title="Productos que llegan al mínimo en 14 días según la demanda pronosticada">Sugerir por pronóstico{lowCount > 0 ? ` (${lowCount} bajo mínimo)` : ''}</button>
          </div>
        ) : <span />}
        <dl className="totals">
          <dt>Subtotal</dt><dd>{money.format(t.subtotal)}</dd>
          <dt>IVA</dt><dd>{money.format(t.tax)}</dd>
          <dt>Total</dt><dd><strong>{money.format(t.total)}</strong></dd>
          {!!order?.payments.length && <><dt>Pagado</dt><dd>{money.format(order.total - order.balance)}</dd><dt>Saldo</dt><dd>{money.format(order.balance)}</dd></>}
        </dl>
      </div>

      {!!order?.payments.length && (
        <table>
          <thead><tr><th>Pago</th><th>Fecha</th><th className="num">Importe</th></tr></thead>
          <tbody>{order.payments.map((p, i) => <tr key={i}><td>{i + 1}</td><td>{fmtDate(p.date)}</td><td className="num">{money.format(p.amount)}</td></tr>)}</tbody>
        </table>
      )}

      {error && <p className="error" role="alert">{error}</p>}

      <div className="actions">
        {draft && <button className="btn secondary" disabled={!!busy || !lines.length} onClick={() => run('save', save)}>{busy === 'save' ? 'Guardando…' : 'Guardar borrador'}</button>}
        {draft && <button className="btn" disabled={!!busy || !lines.length} onClick={() => run('send', send)}>{busy === 'send' ? 'Enviando…' : 'Enviar orden'}</button>}
        {order?.status === 'recibida' && (
          <>
            <input type="number" min="0.01" step="0.01" max={order.balance} value={amount} placeholder={order.balance.toFixed(2)}
              onChange={e => setAmount(e.target.value)} aria-label="Monto del pago" className="amount-input" />
            <button className="btn" disabled={!!busy}
              onClick={() => confirm(`¿Registrar ${amount ? `un abono de ${money.format(Number(amount))}` : `el pago de ${money.format(order.balance)}`} a ${order.folioText}? Se genera la póliza de egreso.`)
                && run('pay', async () => { await api(`/purchases/${order.id}/pay${amount ? `?amount=${amount}` : ''}`, { method: 'POST' }); setAmount(''); return order.id })}>
              {busy === 'pay' ? 'Registrando…' : amount && Number(amount) < order.balance ? 'Registrar abono' : 'Registrar pago'}
            </button>
          </>
        )}
        {order?.status === 'enviada' && (
          <button className="btn" disabled={!!busy}
            onClick={() => confirm(`¿Registrar la recepción de ${order.folioText}? Toda la mercancía entra al inventario.`) && run('receive', action('receive'))}>
            {busy === 'receive' ? 'Registrando…' : 'Registrar recepción'}
          </button>
        )}
        {order && ['borrador', 'enviada'].includes(order.status) && (
          <button className="btn secondary" disabled={!!busy}
            onClick={() => confirm(`¿Cancelar ${order.status === 'borrador' ? 'este borrador' : `la orden ${order.folioText}`}?`) && run('cancel', action('cancel'))}>
            {busy === 'cancel' ? 'Cancelando…' : 'Cancelar orden'}
          </button>
        )}
      </div>
    </div>
  )
}
