import { useEffect, useState } from 'react'
import { api, errorText } from './api'
import { fmtDate, RATES, round, totals } from './totals'
import { useSat } from './sat'

type Row = { id: string; folioText: string; customer: string; issuedAt: string | null; dueDate: string | null; total: number; balance: number; status: string }
type Line = { productId: string | null; description: string; quantity: number; unitPrice: number; taxRate: number }
type Invoice = {
  id: string; folioText: string; customerId: string; status: string; issuedAt: string | null; dueDate: string | null
  paidAt: string | null; subtotal: number; tax: number; total: number; isrWithheld: number; ivaWithheld: number; notes: string | null; lines: Line[]
  paymentMethod: string | null; uuid: string | null; stampedAt: string | null; cancelReason: string | null; substitutedBy: string | null
  replaces: { id: string; folio: number | null; uuid: string | null } | null
  balance: number
  creditNotes: { id: string; folio: number; issuedAt: string; kind: string; reason: string; total: number; uuid: string | null }[]
  payments: { id: string; installment: number; paidAt: string; amount: number; balanceBefore: number; folio: number | null; uuid: string | null }[]
}
type Catalog = {
  customers: { id: string; name: string; creditDays: number }[]
  products: { id: string; sku: string; name: string; unit: string; price: number; stock: number }[]
}

const STATUS: Record<string, { label: string; cls: string }> = {
  borrador: { label: 'Borrador', cls: '' },
  emitida: { label: 'Emitida', cls: '' },
  vencida: { label: 'Vencida', cls: 'bad' },
  pagada: { label: 'Pagada', cls: 'ok' },
  cancelada: { label: 'Cancelada', cls: 'muted-pill' },
}

export default function Facturacion({ currency }: { currency: string }) {
  const money = new Intl.NumberFormat('es-MX', { style: 'currency', currency })
  const [rows, setRows] = useState<Row[]>([])
  const [catalog, setCatalog] = useState<Catalog>({ customers: [], products: [] })
  const [filter, setFilter] = useState('')
  const [open, setOpen] = useState<string | 'new' | null>(null)

  const load = () => api<Row[]>('/invoices').then(setRows)
  const loadCatalog = () => api<Catalog>('/invoices/catalog').then(setCatalog)
  useEffect(() => { load(); loadCatalog() }, [])

  const sum = (s: string[]) => rows.filter(r => s.includes(r.status)).reduce((t, r) => t + (r.status === 'pagada' ? r.total : r.balance), 0)
  const shown = filter ? rows.filter(r => r.status === filter) : rows

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Facturación</h1>
          <div className="muted">Por cobrar {money.format(sum(['emitida', 'vencida']))} · vencido {money.format(sum(['vencida']))}</div>
        </div>
        <select value={filter} onChange={e => setFilter(e.target.value)} aria-label="Filtrar por estado">
          <option value="">Todos los estados</option>
          {Object.entries(STATUS).map(([k, v]) => <option key={k} value={k}>{v.label}</option>)}
        </select>
        <button className="btn" onClick={() => setOpen(open === 'new' ? null : 'new')}>{open === 'new' ? 'Cancelar' : 'Nueva factura'}</button>
      </div>

      {open && (
        <InvoiceEditor key={open} id={open === 'new' ? null : open} catalog={catalog} money={money}
          onClose={() => setOpen(null)}
          onChanged={id => { load(); loadCatalog(); setOpen(id) }}
          onRefresh={() => { load(); loadCatalog() }} />
      )}

      <div className="card">
        <table>
          <thead>
            <tr><th>Folio</th><th>Cliente</th><th>Emisión</th><th>Vence</th><th className="num">Total</th><th className="num">Saldo</th><th>Estado</th></tr>
          </thead>
          <tbody>
            {shown.map(r => (
              <tr key={r.id} tabIndex={0} className="clickable" aria-selected={open === r.id}
                onClick={() => setOpen(r.id)} onKeyDown={e => e.key === 'Enter' && setOpen(r.id)}>
                <td>{r.folioText}</td>
                <td>{r.customer}</td>
                <td>{fmtDate(r.issuedAt)}</td>
                <td>{fmtDate(r.dueDate)}</td>
                <td className="num">{money.format(r.total)}</td>
                <td className="num">{r.balance ? money.format(r.balance) : '—'}</td>
                <td><span className={`pill ${STATUS[r.status].cls}`}>{STATUS[r.status].label}</span></td>
              </tr>
            ))}
            {!shown.length && <tr><td colSpan={7} className="empty">{filter ? 'Ninguna factura con ese estado.' : 'Sin facturas. Crea la primera con «Nueva factura».'}</td></tr>}
          </tbody>
        </table>
      </div>
    </>
  )
}

function InvoiceEditor({ id, catalog, money, onClose, onChanged, onRefresh }: {
  id: string | null; catalog: Catalog; money: Intl.NumberFormat
  onClose: () => void; onChanged: (id: string) => void; onRefresh: () => void
}) {
  const [inv, setInv] = useState<Invoice | null>(null)
  const [customerId, setCustomerId] = useState('')
  const [notes, setNotes] = useState('')
  const [lines, setLines] = useState<Line[]>([{ productId: null, description: '', quantity: 1, unitPrice: 0, taxRate: 0.16 }])
  const [error, setError] = useState('')
  const [busy, setBusy] = useState('')
  const [reason, setReason] = useState('02')
  const [amount, setAmount] = useState('')
  const [crediting, setCrediting] = useState(false)
  const [printing, setPrinting] = useState(false)
  const sat = useSat()
  // Una factura nueva se guarda antes de emitir; si la emisión falla, el siguiente intento edita ese borrador.
  const [savedId, setSavedId] = useState(id)

  useEffect(() => {
    if (!id) return
    api<Invoice>(`/invoices/${id}`).then(i => {
      setInv(i); setCustomerId(i.customerId); setNotes(i.notes ?? ''); setLines(i.lines)
    })
  }, [id])

  const draft = !inv || inv.status === 'borrador'
  const t = totals(lines.map(l => [l.quantity, l.unitPrice, l.taxRate]))
  const set = (i: number, patch: Partial<Line>) => setLines(ls => ls.map((l, j) => (j === i ? { ...l, ...patch } : l)))

  function pickProduct(i: number, productId: string) {
    const p = catalog.products.find(x => x.id === productId)
    set(i, p ? { productId, description: p.name, unitPrice: p.price } : { productId: null })
  }

  async function run(label: string, fn: () => Promise<string>) {
    setError(''); setBusy(label)
    try { onChanged(await fn()) } catch (err) { setError(errorText(err)); onRefresh() }
    setBusy('')
  }

  async function save() {
    const body = { customerId: customerId || catalog.customers[0]?.id, notes, lines }
    if (savedId) { await api(`/invoices/${savedId}`, { method: 'PUT', body }); return savedId }
    const created = (await api<{ id: string }>('/invoices', { method: 'POST', body })).id
    setSavedId(created)
    return created
  }

  const emit = async () => { const saved = await save(); await api(`/invoices/${saved}/emit`, { method: 'POST' }); return saved }

  if (id && !inv) return null

  return (
    <div className="card stack gap-below">
      <div className="detail-head">
        <div>
          <p className="section-title flush">{inv ? `Factura ${inv.folioText}` : 'Nueva factura'}</p>
          {inv && inv.status !== 'borrador' && (
            <div className="muted">
              Emitida {fmtDate(inv.issuedAt)} · vence {fmtDate(inv.dueDate)}
              {inv.paidAt && <> · pagada {fmtDate(inv.paidAt)}</>}
              {inv.paymentMethod && <> · {inv.paymentMethod === 'PPD' ? 'PPD, pago diferido' : 'PUE, una exhibición'}</>}
            </div>
          )}
          {inv?.uuid && (
            <div className="muted">
              CFDI 4.0 · UUID <code>{inv.uuid.toUpperCase()}</code> · timbrado en modo pruebas
              {inv.cancelReason && sat && <> · cancelado: {inv.cancelReason} {sat.cancelReasons[inv.cancelReason]}</>}
              {inv.substitutedBy && <> · sustituido por UUID <code>{inv.substitutedBy.toUpperCase()}</code></>}
            </div>
          )}
          {inv?.replaces && <div className="muted">Sustituye a la factura con UUID <code>{inv.replaces.uuid?.toUpperCase()}</code>; al emitirla, esa se cancela con motivo 01.</div>}
        </div>
        {inv && <span className={`pill ${STATUS[inv.status].cls}`}>{STATUS[inv.status].label}</span>}
        <button className="icon-btn" onClick={onClose} aria-label="Cerrar factura">×</button>
      </div>

      <div className="form-grid">
        <label>Cliente
          <select value={customerId || catalog.customers[0]?.id || ''} onChange={e => setCustomerId(e.target.value)} disabled={!draft}>
            {catalog.customers.map(c => (
              <option key={c.id} value={c.id}>{c.name} {c.creditDays ? `· ${c.creditDays} días` : '· contado'}</option>
            ))}
          </select>
        </label>
        <label className="wide">Notas<input value={notes} onChange={e => setNotes(e.target.value)} disabled={!draft} /></label>
      </div>

      <table className="lines">
        <thead>
          <tr>
            <th>Producto</th><th>Descripción</th><th className="num">Cantidad</th><th className="num">Precio</th>
            <th>IVA</th><th className="num">Importe</th>{draft && <th />}
          </tr>
        </thead>
        <tbody>
          {lines.map((l, i) => {
            const p = catalog.products.find(x => x.id === l.productId)
            return (
              <tr key={i}>
                <td>
                  <select value={l.productId ?? ''} onChange={e => pickProduct(i, e.target.value)} disabled={!draft} aria-label="Producto">
                    <option value="">Sin producto (servicio)</option>
                    {catalog.products.map(x => <option key={x.id} value={x.id}>{x.sku} · {x.name}</option>)}
                  </select>
                  {draft && p && <div className={l.quantity > p.stock ? 'error' : 'muted'}>Existencia {p.stock} {p.unit}</div>}
                </td>
                <td><input value={l.description} onChange={e => set(i, { description: e.target.value })} disabled={!draft} aria-label="Descripción" /></td>
                <td><input className="num" type="number" min="0" step="any" value={l.quantity} onChange={e => set(i, { quantity: Number(e.target.value) })} disabled={!draft} aria-label="Cantidad" /></td>
                <td><input className="num" type="number" min="0" step="0.01" value={l.unitPrice} onChange={e => set(i, { unitPrice: Number(e.target.value) })} disabled={!draft} aria-label="Precio" /></td>
                <td>
                  <select value={l.taxRate} onChange={e => set(i, { taxRate: Number(e.target.value) })} disabled={!draft} aria-label="IVA">
                    {RATES.map(r => <option key={r} value={r}>{r * 100}%</option>)}
                  </select>
                </td>
                <td className="num">{money.format(round(l.quantity * l.unitPrice))}</td>
                {draft && (
                  <td>
                    <button className="icon-btn" onClick={() => setLines(ls => ls.filter((_, j) => j !== i))}
                      disabled={lines.length === 1} aria-label="Quitar partida">×</button>
                  </td>
                )}
              </tr>
            )
          })}
        </tbody>
      </table>

      <div className="invoice-foot">
        {draft
          ? <button className="btn secondary" onClick={() => setLines(ls => [...ls, { productId: null, description: '', quantity: 1, unitPrice: 0, taxRate: 0.16 }])}>Agregar partida</button>
          : <span />}
        <dl className="totals">
          <dt>Subtotal</dt><dd>{money.format(t.subtotal)}</dd>
          <dt>IVA</dt><dd>{money.format(t.tax)}</dd>
          <Withheld p={draft ? undefined : inv} money={money} />
          <dt>Total</dt><dd><strong>{money.format(draft ? t.total : inv!.total)}</strong></dd>
          {!!inv?.creditNotes.length && <><dt>Notas de crédito</dt><dd>−{money.format(inv.creditNotes.reduce((t, n) => t + n.total, 0))}</dd></>}
          {!!inv?.payments.length && <><dt>Abonado</dt><dd>{money.format(inv.payments.reduce((t, p) => t + p.amount, 0))}</dd></>}
          {!!(inv?.payments.length || inv?.creditNotes.length) && <><dt>Saldo</dt><dd>{money.format(inv!.balance)}</dd></>}
        </dl>
      </div>

      {!!inv?.creditNotes.length && (
        <table>
          <thead><tr><th>Nota de crédito</th><th>Fecha</th><th>Tipo</th><th>Motivo</th><th className="num">Importe</th><th>CFDI</th></tr></thead>
          <tbody>
            {inv.creditNotes.map(n => (
              <tr key={n.id}>
                <td>NC-{String(n.folio).padStart(4, '0')}</td>
                <td>{fmtDate(n.issuedAt)}</td>
                <td>{n.kind === 'devolucion' ? 'Devolución' : 'Descuento'}</td>
                <td>{n.reason}</td>
                <td className="num">{money.format(n.total)}</td>
                <td>{n.uuid ? <a href={`/api/invoices/${inv.id}/credit-notes/${n.id}/xml`} download>XML</a> : <span className="muted">—</span>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      {crediting && inv && (
        <form className="form-grid" onSubmit={async e => {
          e.preventDefault()
          const f = Object.fromEntries(new FormData(e.currentTarget)) as Record<string, string>
          await run('credit', async () => {
            await api(`/invoices/${inv.id}/credit-notes`, { method: 'POST', body: { kind: f.kind, amount: Number(f.amount), reason: f.reason } })
            setCrediting(false); return inv.id
          })
        }}>
          <label>Tipo
            <select name="kind" defaultValue="descuento"><option value="descuento">Descuento o bonificación</option><option value="devolucion">Devolución</option></select>
          </label>
          <label>Importe con IVA<input name="amount" type="number" min="0.01" step="0.01" max={inv.balance} required /></label>
          <label className="wide">Motivo<input name="reason" required maxLength={200} placeholder="Ej. descuento por pronto pago" /></label>
          <div className="actions wide flush">
            <button type="button" className="btn secondary" onClick={() => setCrediting(false)}>Cancelar</button>
            <button className="btn" disabled={!!busy}>{busy === 'credit' ? 'Emitiendo…' : 'Emitir nota de crédito'}</button>
          </div>
        </form>
      )}

      {!!inv?.payments.length && (
        <table>
          <thead><tr><th>Parcialidad</th><th>Fecha</th><th className="num">Saldo anterior</th><th className="num">Abono</th><th>Recibo de pago</th></tr></thead>
          <tbody>
            {inv.payments.map(p => (
              <tr key={p.id}>
                <td>{p.installment}</td>
                <td>{fmtDate(p.paidAt)}</td>
                <td className="num">{money.format(p.balanceBefore)}</td>
                <td className="num">{money.format(p.amount)}</td>
                <td>{p.uuid
                  ? <a href={`/api/invoices/${inv.id}/payments/${p.id}/xml`} download>P-{String(p.folio).padStart(4, '0')} · XML</a>
                  : <span className="muted">—</span>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      {error && <p className="error" role="alert">{error}</p>}

      <div className="actions">
        {draft && <button className="btn secondary" disabled={!!busy} onClick={() => run('save', save)}>{busy === 'save' ? 'Guardando…' : 'Guardar borrador'}</button>}
        {draft && <button className="btn" disabled={!!busy} onClick={() => run('emit', emit)}>{busy === 'emit' ? 'Emitiendo…' : 'Emitir factura'}</button>}
        {(inv?.status === 'emitida' || inv?.status === 'vencida') && (
          <>
            {inv.paymentMethod === 'PPD' && (
              <input type="number" min="0.01" step="0.01" max={inv.balance} value={amount} placeholder={inv.balance.toFixed(2)}
                onChange={e => setAmount(e.target.value)} aria-label="Monto del abono" className="amount-input" />
            )}
            <button className="btn" disabled={!!busy} onClick={() => run('pay', async () => {
              await api(`/invoices/${inv.id}/pay${amount ? `?amount=${amount}` : ''}`, { method: 'POST' }); setAmount(''); return inv.id
            })}>{busy === 'pay' ? 'Registrando…' : amount && Number(amount) < inv.balance ? 'Registrar abono' : 'Registrar pago'}</button>
          </>
        )}
        {(inv?.status === 'emitida' || inv?.status === 'vencida') && !crediting && (
          <button className="btn secondary" title="Devolución o descuento: emite un CFDI de egreso que reduce el saldo" onClick={() => setCrediting(true)}>Nota de crédito</button>
        )}
        {inv && inv.status !== 'borrador' && <button className="btn secondary" onClick={() => setPrinting(true)}>Imprimir</button>}
        {inv?.uuid && <a className="btn secondary" href={`/api/invoices/${inv.id}/xml`} download>Descargar XML</a>}
        {inv?.uuid && ['emitida', 'vencida'].includes(inv.status) && !inv.payments.length && !inv.creditNotes.length && (
          <button className="btn secondary" disabled={!!busy} title="Crea un borrador con el mismo contenido; al emitirlo, esta factura se cancela con motivo 01"
            onClick={() => run('substitute', async () => (await api<{ id: string }>(`/invoices/${inv.id}/substitute`, { method: 'POST' })).id)}>
            {busy === 'substitute' ? 'Creando…' : 'Sustituir'}
          </button>
        )}
        {inv?.uuid && ['emitida', 'vencida'].includes(inv.status) && !inv.payments.length && !inv.creditNotes.length && sat && (
          <select value={reason} onChange={e => setReason(e.target.value)} aria-label="Motivo de cancelación del SAT">
            {Object.entries(sat.cancelReasons).filter(([k]) => k !== '01').map(([k, v]) => <option key={k} value={k}>{k} · {v}</option>)}
          </select>
        )}
        {inv && ['borrador', 'emitida', 'vencida'].includes(inv.status) && (
          <button className="btn secondary" disabled={!!busy}
            onClick={() => confirm(inv.status === 'borrador' ? '¿Cancelar este borrador?' : `¿Cancelar la factura ${inv.folioText}? La mercancía regresa al inventario.`)
              && run('cancel', async () => { await api(`/invoices/${inv.id}/cancel${inv.uuid ? `?reason=${reason}` : ''}`, { method: 'POST' }); return inv.id })}>
            {busy === 'cancel' ? 'Cancelando…' : 'Cancelar factura'}
          </button>
        )}
      </div>
      {printing && inv && <PrintSheet id={inv.id} money={money} onDone={() => setPrinting(false)} />}
    </div>
  )
}

type Printable = {
  folioText: string; issuedAt: string; dueDate: string | null; subtotal: number; tax: number; total: number; isrWithheld: number; ivaWithheld: number; notes: string | null
  paymentMethod: string | null; paymentForm: string | null; uuid: string | null; stampedAt: string | null; status: string; qr: string | null
  emitter: { name: string; legalName: string | null; rfc: string | null; zipCode: string | null; regime: string | null }
  receiver: { name: string; rfc: string | null; zipCode: string | null; regime: string | null; use: string }
  lines: { description: string; quantity: number; unitPrice: number; taxRate: number; amount: number }[]
}

// Representación impresa: se abre el diálogo de impresión (o "Guardar como PDF") y se cierra al terminar.
function PrintSheet({ id, money, onDone }: { id: string; money: Intl.NumberFormat; onDone: () => void }) {
  const [p, setP] = useState<Printable | null>(null)
  useEffect(() => { api<Printable>(`/invoices/${id}/print`).then(setP) }, [id])
  useEffect(() => {
    if (!p) return
    const done = () => onDone()
    window.addEventListener('afterprint', done)
    window.print()
    return () => window.removeEventListener('afterprint', done)
  }, [p, onDone])
  if (!p) return null
  return (
    <div className="print-sheet">
      <header>
        <div>
          <h2>{p.emitter.legalName ?? p.emitter.name}</h2>
          {p.emitter.rfc && <div>RFC {p.emitter.rfc} · {p.emitter.regime} · C.P. {p.emitter.zipCode}</div>}
        </div>
        <div className="num">
          <h2>Factura {p.folioText}</h2>
          <div>Emitida {fmtDate(p.issuedAt)}{p.dueDate && <> · vence {fmtDate(p.dueDate)}</>}</div>
          {p.status === 'cancelada' && <strong>CANCELADA</strong>}
        </div>
      </header>
      <section>
        <strong>Receptor:</strong> {p.receiver.name}{p.receiver.rfc && <> · RFC {p.receiver.rfc}</>}
        {p.receiver.regime && <> · {p.receiver.regime} · C.P. {p.receiver.zipCode} · Uso {p.receiver.use}</>}
      </section>
      <table>
        <thead><tr><th>Descripción</th><th className="num">Cantidad</th><th className="num">Precio</th><th className="num">IVA</th><th className="num">Importe</th></tr></thead>
        <tbody>{p.lines.map((l, i) => <tr key={i}><td>{l.description}</td><td className="num">{l.quantity}</td><td className="num">{money.format(l.unitPrice)}</td><td className="num">{l.taxRate * 100}%</td><td className="num">{money.format(l.amount)}</td></tr>)}</tbody>
      </table>
      <dl className="totals">
        <dt>Subtotal</dt><dd>{money.format(p.subtotal)}</dd>
        <dt>IVA</dt><dd>{money.format(p.tax)}</dd>
        <Withheld p={p} money={money} />
        <dt>Total</dt><dd><strong>{money.format(p.total)}</strong></dd>
      </dl>
      {p.paymentMethod && <p>Método de pago {p.paymentMethod} · forma de pago {p.paymentForm} · moneda MXN</p>}
      {p.qr && <div className="qr" dangerouslySetInnerHTML={{ __html: p.qr }} />}
      {p.uuid
        ? <p className="small">Este documento es una representación impresa de un CFDI 4.0. UUID {p.uuid.toUpperCase()} · timbrado {fmtDate(p.stampedAt)}. Timbrado en modo pruebas: sin validez fiscal.</p>
        : <p className="small">Documento interno sin validez fiscal.</p>}
      {p.notes && <p>{p.notes}</p>}
    </div>
  )
}

// Retenciones del cliente persona moral a una persona física (ISR e IVA); ya vienen restadas del total.
function Withheld({ p, money }: { p?: { isrWithheld: number; ivaWithheld: number } | null; money: Intl.NumberFormat }) {
  if (!p || p.isrWithheld + p.ivaWithheld === 0) return null
  return <><dt>ISR retenido</dt><dd>−{money.format(p.isrWithheld)}</dd><dt>IVA retenido</dt><dd>−{money.format(p.ivaWithheld)}</dd></>
}
