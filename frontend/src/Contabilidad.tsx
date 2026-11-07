import { useEffect, useState, type FormEvent } from 'react'
import { api, errorText } from './api'
import { fmtDate, iso } from './totals'
import Jobs from './Tareas'

type Account = { id: string; code: string; name: string; type: string; active: boolean; satCode: string | null }
type EntryRow = { id: string; folioText: string; date: string; kind: string; concept: string; source: string; total: number; reversed: boolean; isReversal: boolean }
type Entry = { id: string; folioText: string; date: string; kind: string; concept: string; source: string; lines: { code: string; account: string; debit: number; credit: number; description: string | null }[] }
type Balance = { id: string; code: string; name: string; type: string; opening: number; debit: number; credit: number; closing: number }
type Line = { accountId: string; debit: number; credit: number; description: string }

const TYPES = ['activo', 'pasivo', 'capital', 'ingreso', 'gasto']
const TYPE_LABEL: Record<string, string> = { activo: 'Activo', pasivo: 'Pasivo', capital: 'Capital', ingreso: 'Ingreso', gasto: 'Costo o gasto' }
const SOURCE: Record<string, string> = { manual: 'Manual', factura: 'Factura', compra: 'Compra', nomina: 'Nómina', apertura: 'Apertura', banco: 'Banco', impuesto: 'Impuesto', cierre: 'Cierre' }
const KIND: Record<string, string> = { diario: 'Diario', ingreso: 'Ingreso', egreso: 'Egreso' }
const now = new Date()
const yearStart = `${now.getFullYear()}-01-01`

export default function Contabilidad({ currency }: { currency: string }) {
  const money = new Intl.NumberFormat('es-MX', { style: 'currency', currency })
  const [tab, setTab] = useState<'polizas' | 'balanza' | 'estados' | 'impuestos' | 'sat' | 'pronostico' | 'conciliacion' | 'bancos' | 'cuentas'>('polizas')
  const [accounts, setAccounts] = useState<Account[]>([])
  const loadAccounts = () => api<Account[]>('/accounting/accounts').then(setAccounts)
  useEffect(() => { loadAccounts() }, [])

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Contabilidad</h1>
          <div className="muted">Pólizas automáticas de facturas, cobros, compras y nómina, más pólizas manuales.</div>
        </div>
      </div>
      <div className="tabs" role="tablist">
        {([['polizas', 'Pólizas'], ['balanza', 'Balanza'], ['estados', 'Estados financieros'], ['impuestos', 'Impuestos y cuotas'], ['sat', 'SAT y cierre'], ['pronostico', 'Pronóstico de caja'], ['conciliacion', 'Conciliación'], ['bancos', 'Bancos'], ['cuentas', 'Catálogo de cuentas']] as const).map(([k, l]) => (
          <button key={k} role="tab" aria-selected={tab === k} onClick={() => setTab(k)}>{l}</button>
        ))}
      </div>
      {tab === 'polizas' && <Polizas accounts={accounts.filter(a => a.active)} money={money} />}
      {tab === 'balanza' && <Balanza money={money} />}
      {tab === 'estados' && <Estados money={money} />}
      {tab === 'impuestos' && <Obligaciones money={money} />}
      {tab === 'sat' && <Sat money={money} />}
      {tab === 'pronostico' && <Pronostico money={money} />}
      {tab === 'conciliacion' && <Conciliacion />}
      {tab === 'bancos' && <Bancos money={money} />}
      {tab === 'cuentas' && <Cuentas accounts={accounts} onChanged={loadAccounts} />}
    </>
  )
}

function Period({ from, to, onChange }: { from: string; to: string; onChange: (from: string, to: string) => void }) {
  return (
    <div className="period">
      <label>Desde<input type="date" value={from} max={to} onChange={e => onChange(e.target.value, to)} /></label>
      <label>Hasta<input type="date" value={to} min={from} onChange={e => onChange(from, e.target.value)} /></label>
    </div>
  )
}

function Polizas({ accounts, money }: { accounts: Account[]; money: Intl.NumberFormat }) {
  const [rows, setRows] = useState<EntryRow[]>([])
  const [range, setRange] = useState({ from: iso(new Date(now.getFullYear(), now.getMonth() - 2, 1)), to: iso(now) })
  const [open, setOpen] = useState<string | 'new' | null>(null)
  const load = () => api<EntryRow[]>(`/accounting/entries?from=${range.from}&to=${range.to}`).then(setRows)
  useEffect(() => { load() }, [range]) // eslint-disable-line react-hooks/exhaustive-deps

  return (
    <div className="stack">
      <div className="page-head flush">
        <Period from={range.from} to={range.to} onChange={(from, to) => setRange({ from, to })} />
        <button className="btn" onClick={() => setOpen(open === 'new' ? null : 'new')}>{open === 'new' ? 'Cancelar' : 'Nueva póliza'}</button>
      </div>
      {open === 'new' && <NewEntry accounts={accounts} money={money} onSaved={id => { load(); setOpen(id) }} />}
      {open && open !== 'new' && <EntryDetail key={open} id={open} money={money} onClose={() => setOpen(null)} onChanged={id => { load(); setOpen(id) }} />}
      <div className="card">
        <table>
          <thead><tr><th>Folio</th><th>Fecha</th><th>Tipo</th><th>Concepto</th><th>Origen</th><th className="num">Importe</th></tr></thead>
          <tbody>
            {rows.map(r => (
              <tr key={r.id} tabIndex={0} className="clickable" aria-selected={open === r.id}
                onClick={() => setOpen(r.id)} onKeyDown={e => e.key === 'Enter' && setOpen(r.id)}>
                <td>{r.folioText}</td>
                <td>{fmtDate(r.date)}</td>
                <td>{KIND[r.kind]}</td>
                <td>{r.concept}{r.reversed && <span className="pill muted-pill inline">Revertida</span>}{r.isReversal && <span className="pill inline">Reversa</span>}</td>
                <td>{SOURCE[r.source] ?? r.source}</td>
                <td className="num">{money.format(r.total)}</td>
              </tr>
            ))}
            {!rows.length && <tr><td colSpan={6} className="empty">Sin pólizas en el periodo. Cambia las fechas o registra una con «Nueva póliza».</td></tr>}
          </tbody>
        </table>
      </div>
    </div>
  )
}

function EntryDetail({ id, money, onClose, onChanged }: { id: string; money: Intl.NumberFormat; onClose: () => void; onChanged: (id: string) => void }) {
  const [e, setE] = useState<Entry | null>(null)
  const [error, setError] = useState('')
  useEffect(() => { api<Entry>(`/accounting/entries/${id}`).then(setE) }, [id])
  if (!e) return null

  async function reverse() {
    if (!confirm(`¿Revertir la póliza ${e!.folioText}? Se crea una póliza con cargos y abonos invertidos.`)) return
    setError('')
    try { onChanged((await api<{ id: string }>(`/accounting/entries/${id}/reverse`, { method: 'POST' })).id) } catch (err) { setError(errorText(err)) }
  }

  return (
    <div className="card stack">
      <div className="detail-head">
        <div>
          <p className="section-title flush">Póliza {e.folioText} · {KIND[e.kind]}</p>
          <div className="muted">{fmtDate(e.date)} · {e.concept}</div>
        </div>
        <button className="icon-btn" onClick={onClose} aria-label="Cerrar póliza">×</button>
      </div>
      <table>
        <thead><tr><th>Cuenta</th><th>Descripción</th><th className="num">Cargo</th><th className="num">Abono</th></tr></thead>
        <tbody>
          {e.lines.map((l, i) => (
            <tr key={i}>
              <td>{l.code} {l.account}</td><td>{l.description ?? ''}</td>
              <td className="num">{l.debit ? money.format(l.debit) : ''}</td><td className="num">{l.credit ? money.format(l.credit) : ''}</td>
            </tr>
          ))}
          <tr className="total-row">
            <td colSpan={2}>Sumas iguales</td>
            <td className="num">{money.format(e.lines.reduce((s, l) => s + l.debit, 0))}</td>
            <td className="num">{money.format(e.lines.reduce((s, l) => s + l.credit, 0))}</td>
          </tr>
        </tbody>
      </table>
      {error && <p className="error" role="alert">{error}</p>}
      {e.source === 'manual' && <div className="actions"><button className="btn secondary" onClick={reverse}>Revertir póliza</button></div>}
    </div>
  )
}

function NewEntry({ accounts, money, onSaved }: { accounts: Account[]; money: Intl.NumberFormat; onSaved: (id: string) => void }) {
  const blank = (): Line => ({ accountId: accounts[0]?.id ?? '', debit: 0, credit: 0, description: '' })
  const [date, setDate] = useState(iso(now))
  const [kind, setKind] = useState('diario')
  const [concept, setConcept] = useState('')
  const [lines, setLines] = useState<Line[]>([blank(), blank()])
  const [error, setError] = useState('')
  const set = (i: number, patch: Partial<Line>) => setLines(ls => ls.map((l, j) => (j === i ? { ...l, ...patch } : l)))
  const debit = lines.reduce((s, l) => s + l.debit, 0), credit = lines.reduce((s, l) => s + l.credit, 0)
  const balanced = Math.abs(debit - credit) < 0.005 && debit > 0

  async function submit(ev: FormEvent) {
    ev.preventDefault()
    setError('')
    try { onSaved((await api<{ id: string }>('/accounting/entries', { method: 'POST', body: { date, kind, concept, lines } })).id) } catch (err) { setError(errorText(err)) }
  }

  return (
    <form className="card stack" onSubmit={submit}>
      <p className="section-title flush">Nueva póliza</p>
      <div className="form-grid">
        <label>Fecha<input type="date" value={date} onChange={e => setDate(e.target.value)} required /></label>
        <label>Tipo
          <select value={kind} onChange={e => setKind(e.target.value)}>{Object.entries(KIND).map(([k, v]) => <option key={k} value={k}>{v}</option>)}</select>
        </label>
        <label className="wide">Concepto<input value={concept} onChange={e => setConcept(e.target.value)} required /></label>
      </div>
      <table className="lines">
        <thead><tr><th>Cuenta</th><th>Descripción</th><th className="num">Cargo</th><th className="num">Abono</th><th /></tr></thead>
        <tbody>
          {lines.map((l, i) => (
            <tr key={i}>
              <td>
                <select value={l.accountId} onChange={e => set(i, { accountId: e.target.value })} aria-label="Cuenta">
                  {accounts.map(a => <option key={a.id} value={a.id}>{a.code} {a.name}</option>)}
                </select>
              </td>
              <td><input value={l.description} onChange={e => set(i, { description: e.target.value })} aria-label="Descripción" /></td>
              <td><input className="num" type="number" min="0" step="0.01" value={l.debit || ''} onChange={e => set(i, { debit: Number(e.target.value), credit: 0 })} aria-label="Cargo" /></td>
              <td><input className="num" type="number" min="0" step="0.01" value={l.credit || ''} onChange={e => set(i, { credit: Number(e.target.value), debit: 0 })} aria-label="Abono" /></td>
              <td><button type="button" className="icon-btn" disabled={lines.length <= 2} onClick={() => setLines(ls => ls.filter((_, j) => j !== i))} aria-label="Quitar partida">×</button></td>
            </tr>
          ))}
          <tr className="total-row">
            <td colSpan={2}>{balanced ? 'Cuadra ✓' : <span className="error">Diferencia {money.format(debit - credit)}</span>}</td>
            <td className="num">{money.format(debit)}</td><td className="num">{money.format(credit)}</td><td />
          </tr>
        </tbody>
      </table>
      {error && <p className="error" role="alert">{error}</p>}
      <div className="actions">
        <button type="button" className="btn secondary" onClick={() => setLines(ls => [...ls, blank()])}>Agregar partida</button>
        <button className="btn" disabled={!balanced}>Registrar póliza</button>
      </div>
    </form>
  )
}

function useBalances(from: string, to: string) {
  const [rows, setRows] = useState<Balance[]>([])
  useEffect(() => { api<Balance[]>(`/accounting/balances?from=${from}&to=${to}`).then(setRows) }, [from, to])
  return rows
}

function Balanza({ money }: { money: Intl.NumberFormat }) {
  const [range, setRange] = useState({ from: yearStart, to: iso(now) })
  const rows = useBalances(range.from, range.to).filter(r => r.opening || r.debit || r.credit || r.closing)
  const sum = (k: 'debit' | 'credit') => rows.reduce((s, r) => s + r[k], 0)

  return (
    <div className="stack">
      <Period from={range.from} to={range.to} onChange={(from, to) => setRange({ from, to })} />
      <div className="card">
        <table>
          <thead><tr><th>Cuenta</th><th>Tipo</th><th className="num">Saldo inicial</th><th className="num">Cargos</th><th className="num">Abonos</th><th className="num">Saldo final</th></tr></thead>
          <tbody>
            {rows.map((r, i) => (
              <tr key={r.id} className="row-in" style={{ animationDelay: `${i * 25}ms` }}>
                <td>{r.code} {r.name}</td><td>{TYPE_LABEL[r.type]}</td>
                <td className="num">{money.format(r.opening)}</td><td className="num">{money.format(r.debit)}</td>
                <td className="num">{money.format(r.credit)}</td><td className="num">{money.format(r.closing)}</td>
              </tr>
            ))}
            <tr className="total-row">
              <td colSpan={3}>{Math.abs(sum('debit') - sum('credit')) < 0.005 ? 'Sumas iguales ✓' : <span className="error">Descuadre</span>}</td>
              <td className="num">{money.format(sum('debit'))}</td><td className="num">{money.format(sum('credit'))}</td><td />
            </tr>
          </tbody>
        </table>
      </div>
    </div>
  )
}

function Estados({ money }: { money: Intl.NumberFormat }) {
  const [range, setRange] = useState({ from: yearStart, to: iso(now) })
  const period = useBalances(range.from, range.to)
  // El balance general es a la fecha final y acumula todo lo anterior.
  const all = useBalances('2000-01-01', range.to)
  const movement = (r: Balance) => r.closing - r.opening
  const of = (rows: Balance[], type: string) => rows.filter(r => r.type === type)
  const total = (rows: Balance[], f: (r: Balance) => number) => rows.reduce((s, r) => s + f(r), 0)

  const income = of(period, 'ingreso'), expense = of(period, 'gasto')
  const profit = total(income, movement) - total(expense, movement)
  const retained = total(of(all, 'ingreso'), r => r.closing) - total(of(all, 'gasto'), r => r.closing)
  const assets = total(of(all, 'activo'), r => r.closing)
  const liabilities = total(of(all, 'pasivo'), r => r.closing)
  const equity = total(of(all, 'capital'), r => r.closing) + retained

  const Section = ({ title, rows, value }: { title: string; rows: Balance[]; value: (r: Balance) => number }) => (
    <>
      <tr className="group-row"><td colSpan={2}>{title}</td></tr>
      {rows.filter(r => value(r) !== 0).map(r => <tr key={r.id}><td>{r.code} {r.name}</td><td className="num">{money.format(value(r))}</td></tr>)}
      <tr className="total-row"><td>Total {title.toLowerCase()}</td><td className="num">{money.format(total(rows, value))}</td></tr>
    </>
  )

  return (
    <div className="stack">
      <Period from={range.from} to={range.to} onChange={(from, to) => setRange({ from, to })} />
      <div className="chart-grid">
        <div className="card">
          <p className="section-title">Estado de resultados</p>
          <table>
            <tbody>
              <Section title="Ingresos" rows={income} value={movement} />
              <Section title="Costos y gastos" rows={expense} value={movement} />
              <tr className="total-row strong"><td>Utilidad del periodo</td><td className="num">{money.format(profit)}</td></tr>
            </tbody>
          </table>
        </div>
        <div className="card">
          <p className="section-title">Balance general al {fmtDate(range.to)}</p>
          <table>
            <tbody>
              <Section title="Activo" rows={of(all, 'activo')} value={r => r.closing} />
              <Section title="Pasivo" rows={of(all, 'pasivo')} value={r => r.closing} />
              <Section title="Capital" rows={of(all, 'capital')} value={r => r.closing} />
              <tr><td>Resultados acumulados</td><td className="num">{money.format(retained)}</td></tr>
              <tr className="total-row strong"><td>Pasivo + capital</td><td className="num">{money.format(liabilities + equity)}</td></tr>
            </tbody>
          </table>
          <p className={Math.abs(assets - liabilities - equity) < 0.005 ? 'muted' : 'error'}>
            {Math.abs(assets - liabilities - equity) < 0.005 ? 'Activo = pasivo + capital ✓' : `Descuadre de ${money.format(assets - liabilities - equity)}`}
          </p>
        </div>
      </div>
    </div>
  )
}

function Cuentas({ accounts, onChanged }: { accounts: Account[]; onChanged: () => void }) {
  const [error, setError] = useState('')

  async function add(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const form = e.currentTarget
    setError('')
    try { await api('/accounting/accounts', { method: 'POST', body: Object.fromEntries(new FormData(form)) }); form.reset(); onChanged() } catch (err) { setError(errorText(err)) }
  }

  async function save(a: Account, patch: Partial<Account>) {
    setError('')
    try { await api(`/accounting/accounts/${a.id}`, { method: 'PUT', body: { ...a, ...patch } }); onChanged() } catch (err) { setError(errorText(err)) }
  }

  return (
    <div className="stack">
      <form className="card form-row" onSubmit={add}>
        <label>Código<input name="code" required maxLength={20} /></label>
        <label>Nombre<input name="name" required /></label>
        <label>Tipo<select name="type">{TYPES.map(t => <option key={t} value={t}>{TYPE_LABEL[t]}</option>)}</select></label>
        <label>Código agrupador SAT<input name="satCode" placeholder="601.84" pattern="\d{3}(\.\d{2})?" /></label>
        <button className="btn">Agregar cuenta</button>
        {error && <p className="error wide" role="alert">{error}</p>}
      </form>
      <div className="card">
        <table>
          <thead><tr><th>Código</th><th>Cuenta</th><th>Tipo</th><th>Código agrupador</th><th>Estado</th><th /></tr></thead>
          <tbody>
            {accounts.map(a => (
              <tr key={a.id}>
                <td>{a.code}</td><td>{a.name}</td><td>{TYPE_LABEL[a.type]}</td>
                <td>
                  <input className="amount-input" defaultValue={a.satCode ?? ''} placeholder="Sin código" aria-label={`Código agrupador de ${a.name}`}
                    onBlur={e => e.target.value.trim() !== (a.satCode ?? '') && save(a, { satCode: e.target.value.trim() || null })} />
                </td>
                <td><span className={`pill ${a.active ? 'ok' : 'muted-pill'}`}>{a.active ? 'Activa' : 'Inactiva'}</span></td>
                <td><button className="btn secondary" onClick={() => save(a, { active: !a.active })}>{a.active ? 'Desactivar' : 'Activar'}</button></td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}

type Cash = { current: number; forecast: { month: string; balance: number }[]; items: { concept: string; amount: number }[]; assumptions: string[] }
const monthLong = new Intl.DateTimeFormat('es-MX', { month: 'long', year: 'numeric', timeZone: 'UTC' })

// El cálculo es del servicio de pronósticos; aquí solo se muestra con sus supuestos a la vista.
function Pronostico({ money }: { money: Intl.NumberFormat }) {
  const [c, setC] = useState<Cash | null>(null)
  useEffect(() => { api<Cash>('/forecast/cash').then(setC) }, [])
  if (!c) return null
  return (
    <div className="stack">
      <div className="card kpis">
        <div><div className="muted">Caja y bancos hoy</div><div className="kpi small">{money.format(c.current)}</div></div>
        {c.forecast.map(f => (
          <div key={f.month}><div className="muted">Cierre de {monthLong.format(new Date(`${f.month}-01`))}</div><div className="kpi small">{money.format(f.balance)}</div></div>
        ))}
      </div>
      <div className="card">
        <p className="section-title">Qué mueve la proyección</p>
        <table>
          <tbody>
            {c.items.map(i => <tr key={i.concept}><td>{i.concept}</td><td className="num">{money.format(i.amount)}</td></tr>)}
            <tr className="total-row"><td>Variación a {c.forecast.length} meses</td><td className="num">{money.format(c.items.reduce((s, i) => s + i.amount, 0))}</td></tr>
          </tbody>
        </table>
      </div>
      <div className="card">
        <p className="section-title">Supuestos</p>
        <ul className="muted">{c.assumptions.map(a => <li key={a}>{a}</li>)}</ul>
      </div>
    </div>
  )
}

function Conciliacion() {
  const [refresh, setRefresh] = useState(0)
  const [error, setError] = useState('')
  async function start() {
    setError('')
    try { await api('/jobs', { method: 'POST', body: { kind: 'conciliacion' } }); setRefresh(n => n + 1) } catch (err) { setError(errorText(err)) }
  }
  return (
    <div className="card stack">
      <div className="page-head flush">
        <div className="muted">Revisa que pólizas, existencias, facturas y órdenes de compra cuadren con los libros. Corre en segundo plano.</div>
        <button className="btn" onClick={start}>Ejecutar conciliación</button>
      </div>
      {error && <p className="error" role="alert">{error}</p>}
      <Jobs kind="conciliacion" refresh={refresh} />
    </div>
  )
}

type Suggestion = { kind: string; id: string; label: string; amount: number; alreadyPaid: boolean }
type BankTx = { id: string; date: string; description: string; reference: string | null; amount: number; status: string; matchKind: string | null; suggestions: Suggestion[] }
const BANK_STATUS: Record<string, string> = { pendiente: 'Pendientes', conciliado: 'Conciliados', ignorado: 'Ignorados', '': 'Todos' }

// Estado de cuenta en CSV: cada depósito o retiro se liga a una factura, orden de compra o nómina del mismo importe.
function Bancos({ money }: { money: Intl.NumberFormat }) {
  const [rows, setRows] = useState<BankTx[]>([])
  const [status, setStatus] = useState('pendiente')
  const [pick, setPick] = useState<Record<string, string>>({})
  const [msg, setMsg] = useState({ error: '', ok: '' })
  const load = () => api<BankTx[]>(`/bank?status=${status}`).then(setRows)
  useEffect(() => { load() }, [status]) // eslint-disable-line react-hooks/exhaustive-deps

  async function upload(file: File | undefined) {
    if (!file) return
    setMsg({ error: '', ok: '' })
    try {
      const r = await api<{ added: number; skipped: number; errors: string[] }>('/bank/import', { method: 'POST', body: { csv: await file.text() } })
      setMsg({ ok: `${r.added} movimientos nuevos${r.skipped ? `, ${r.skipped} ya importados` : ''}.`, error: r.errors.join(' ') })
      load()
    } catch (err) { setMsg({ error: errorText(err), ok: '' }) }
  }

  async function act(t: BankTx, path: string, body?: unknown) {
    setMsg({ error: '', ok: '' })
    try { await api(`/bank/${t.id}/${path}`, { method: 'POST', body }); load() } catch (err) { setMsg({ error: errorText(err), ok: '' }) }
  }

  return (
    <div className="stack">
      <div className="card stack">
        <div className="page-head flush">
          <div className="muted">Importa el estado de cuenta en CSV con columnas Fecha, Descripción, Referencia (opcional) y Monto, o Cargo y Abono. Los movimientos repetidos se omiten.</div>
          <label className="btn">Importar CSV<input type="file" accept=".csv,text/csv" hidden onChange={e => { upload(e.target.files?.[0]); e.target.value = '' }} /></label>
        </div>
        {msg.ok && <p className="muted flush" role="status">{msg.ok}</p>}
        {msg.error && <p className="error" role="alert">{msg.error}</p>}
      </div>
      <div className="chips" role="tablist" aria-label="Estado de los movimientos">
        {Object.entries(BANK_STATUS).map(([k, l]) => <button key={k} role="tab" aria-selected={status === k} className="chip" onClick={() => setStatus(k)}>{l}</button>)}
      </div>
      <div className="card">
        <table>
          <thead><tr><th>Fecha</th><th>Descripción</th><th>Referencia</th><th className="num">Monto</th><th>Conciliar con</th><th /></tr></thead>
          <tbody>
            {rows.map(t => {
              const chosen = pick[t.id] ?? (t.suggestions[0] ? `${t.suggestions[0].kind}:${t.suggestions[0].id}` : '')
              return (
                <tr key={t.id}>
                  <td>{fmtDate(t.date)}</td><td>{t.description}</td><td>{t.reference ?? '—'}</td>
                  <td className="num">{money.format(t.amount)}</td>
                  <td>
                    {t.status !== 'pendiente' ? <span className={`pill ${t.status === 'conciliado' ? 'ok' : 'muted-pill'}`}>{t.status === 'conciliado' ? `Conciliado · ${t.matchKind}` : 'Ignorado'}</span>
                      : t.suggestions.length ? (
                        <select value={chosen} onChange={e => setPick(p => ({ ...p, [t.id]: e.target.value }))} aria-label={`Documento para ${t.description}`}>
                          {t.suggestions.map(s => <option key={s.id} value={`${s.kind}:${s.id}`}>{s.label}{s.alreadyPaid ? ' (ya registrado)' : ''}</option>)}
                        </select>
                      ) : <span className="muted">Sin coincidencias de importe</span>}
                  </td>
                  <td>
                    {t.status === 'pendiente' && (
                      <div className="actions flush nowrap">
                        {chosen && <button className="btn secondary" onClick={() => { const [kind, id] = chosen.split(':'); act(t, 'match', { kind, id }) }}>Conciliar</button>}
                        {!t.suggestions.length && t.amount < 0 && <button className="btn secondary" onClick={() => act(t, 'expense')} title="Póliza de egreso a gastos generales">Registrar gasto</button>}
                        <button className="btn secondary" onClick={() => act(t, 'ignore')}>Ignorar</button>
                      </div>
                    )}
                  </td>
                </tr>
              )
            })}
            {!rows.length && <tr><td colSpan={6} className="empty">Sin movimientos. Importa el estado de cuenta en CSV para conciliar.</td></tr>}
          </tbody>
        </table>
      </div>
    </div>
  )
}

type Obligation = { kind: string; label: string; balance: number; credit: number; due: number }

function Obligaciones({ money }: { money: Intl.NumberFormat }) {
  const [at, setAt] = useState(iso(now))
  const [rows, setRows] = useState<Obligation[]>([])
  const [amounts, setAmounts] = useState<Record<string, string>>({})
  const [msg, setMsg] = useState({ error: '', ok: '' })
  const [busy, setBusy] = useState('')
  const load = () => api<Obligation[]>(`/taxes/obligations?at=${at}`).then(setRows)
  useEffect(() => { load() }, [at]) // eslint-disable-line react-hooks/exhaustive-deps

  async function pay(o: Obligation) {
    const amount = o.kind === 'iva' ? undefined : Number(amounts[o.kind] || o.due)
    if (!confirm(`¿Registrar el pago de ${o.label.toLowerCase()} por ${money.format(amount ?? o.due)} con fecha ${fmtDate(at)}? Se genera la póliza de egreso.`)) return
    setBusy(o.kind); setMsg({ error: '', ok: '' })
    try {
      await api(`/taxes/obligations/${o.kind}/pay`, { method: 'POST', body: { date: at, amount } })
      setAmounts(a => ({ ...a, [o.kind]: '' })); setMsg({ error: '', ok: 'Pago registrado.' }); await load()
    } catch (err) { setMsg({ error: errorText(err), ok: '' }) }
    setBusy('')
  }

  return (
    <div className="stack">
      <div className="card stack">
        <div className="page-head flush">
          <div className="muted">Saldos por enterar según la contabilidad al corte. Retenciones y cuotas admiten pagos parciales; el IVA compensa primero el acreditable.</div>
          <label>Fecha de corte y pago<input type="date" value={at} onChange={e => setAt(e.target.value)} /></label>
        </div>
        <table>
          <thead><tr><th>Obligación</th><th className="num">Saldo</th><th className="num">Acreditable</th><th className="num">Por pagar</th><th className="num">Importe</th><th /></tr></thead>
          <tbody>
            {rows.map(o => (
              <tr key={o.kind}>
                <td>{o.label}</td>
                <td className="num">{money.format(o.balance)}</td>
                <td className="num">{o.kind === 'iva' ? money.format(o.credit) : '—'}</td>
                <td className="num"><strong>{money.format(o.due)}</strong>{o.kind === 'iva' && o.credit > o.balance && <div className="muted">Saldo a favor {money.format(o.credit - o.balance)}</div>}</td>
                <td className="num">
                  {o.kind === 'iva' ? <span className="muted">Total</span> : (
                    <input className="amount-input" type="number" min="0.01" step="0.01" max={o.due} placeholder={o.due.toFixed(2)} value={amounts[o.kind] ?? ''}
                      disabled={o.due <= 0} onChange={e => setAmounts(a => ({ ...a, [o.kind]: e.target.value }))} aria-label={`Importe a pagar de ${o.label}`} />
                  )}
                </td>
                <td><button className="btn" disabled={!!busy || o.balance <= 0} onClick={() => pay(o)}>{busy === o.kind ? 'Registrando…' : 'Registrar pago'}</button></td>
              </tr>
            ))}
            {!rows.length && <tr><td colSpan={6} className="empty">Cargando obligaciones…</td></tr>}
          </tbody>
        </table>
        {msg.error && <p className="error" role="alert">{msg.error}</p>}
        {msg.ok && <p className="muted flush" role="status">{msg.ok}</p>}
        <p className="muted flush">El IVA se calcula devengado (al facturar y al recibir compras). La declaración ante el SAT se presenta sobre flujo de efectivo: confirma el importe con tu contador antes de pagar.</p>
      </div>
    </div>
  )
}

type Provisional = { applies: boolean; income: number; coefficient: number; profit: number; isr: number; paid: number; withheld: number; due: number }

// Contabilidad electrónica (XML de catálogo y balanza), ISR provisional y cierre del ejercicio.
function Sat({ money }: { money: Intl.NumberFormat }) {
  const [period, setPeriod] = useState(iso(now).slice(0, 7))
  const [prov, setProv] = useState<Provisional | null>(null)
  const [coef, setCoef] = useState('')
  const [closed, setClosed] = useState<string | null>(null)
  const [msg, setMsg] = useState({ error: '', ok: '' })
  const [year, month] = period.split('-').map(Number)
  const lastDay = iso(new Date(year, month, 0))
  const closeYear = (closed ? Number(closed.slice(0, 4)) : now.getFullYear() - 1) + (closed ? 1 : 0)

  const load = () => {
    api<Provisional>(`/taxes/provisional?at=${lastDay}`).then(p => { setProv(p); setCoef(String(p.coefficient)) })
    api<{ closedThrough: string | null }>('/accounting/close').then(c => setClosed(c.closedThrough))
  }
  useEffect(load, [lastDay])

  async function run(action: () => Promise<unknown>, ok: string) {
    setMsg({ error: '', ok: '' })
    try { await action(); setMsg({ error: '', ok }); load() } catch (err) { setMsg({ error: errorText(err), ok: '' }) }
  }

  async function download(doc: 'catalogo' | 'balanza') {
    setMsg({ error: '', ok: '' })
    const res = await fetch(`/api/accounting/sat/${doc}?year=${year}&month=${month}`)
    if (!res.ok) { const p = await res.json().catch(() => ({})); setMsg({ error: p.detail ?? `Error ${res.status}`, ok: '' }); return }
    const a = document.createElement('a')
    a.href = URL.createObjectURL(await res.blob())
    a.download = res.headers.get('content-disposition')?.match(/filename=([^;]+)/)?.[1] ?? `${doc}.xml`
    a.click()
    URL.revokeObjectURL(a.href)
  }

  return (
    <div className="stack">
      <div className="card stack">
        <div className="page-head flush">
          <div>
            <h2 className="flush">Contabilidad electrónica</h2>
            <div className="muted">Catálogo y balanza de comprobación (versión 1.3) del mes para el buzón tributario.</div>
          </div>
          <label>Periodo<input type="month" value={period} onChange={e => e.target.value && setPeriod(e.target.value)} /></label>
        </div>
        <div className="actions">
          <button className="btn secondary" onClick={() => download('catalogo')}>Descargar catálogo XML</button>
          <button className="btn secondary" onClick={() => download('balanza')}>Descargar balanza XML</button>
        </div>
        <p className="muted flush">Cada cuenta con movimientos necesita su código agrupador; asígnalo en el catálogo de cuentas.</p>
      </div>

      <div className="card stack">
        <h2 className="flush">ISR provisional del periodo</h2>
        {prov && !prov.applies && <p className="muted flush">El cálculo automático aplica a personas morales (RFC de 12 caracteres). Personas físicas y RESICO: consulta a tu contador.</p>}
        {prov?.applies && (
          <>
            <dl className="kv">
              <dt>Ingresos acumulados del ejercicio</dt><dd>{money.format(prov.income)}</dd>
              <dt>Coeficiente de utilidad</dt><dd>{prov.coefficient}</dd>
              <dt>Utilidad fiscal estimada</dt><dd>{money.format(prov.profit)}</dd>
              <dt>ISR (30%)</dt><dd>{money.format(prov.isr)}</dd>
              <dt>Pagos provisionales anteriores</dt><dd>{prov.paid ? `−${money.format(prov.paid)}` : money.format(0)}</dd>
              <dt>ISR retenido por clientes</dt><dd>{prov.withheld ? `−${money.format(prov.withheld)}` : money.format(0)}</dd>
              <dt>A pagar</dt><dd><strong>{money.format(prov.due)}</strong></dd>
            </dl>
            <form className="actions" onSubmit={e => { e.preventDefault(); run(() => api('/taxes/coefficient', { method: 'PUT', body: { profitCoefficient: Number(coef) } }), 'Coeficiente guardado.') }}>
              <label>Coeficiente de utilidad<input className="amount-input" type="number" min="0" max="0.9999" step="0.0001" value={coef} onChange={e => setCoef(e.target.value)} /></label>
              <button className="btn secondary">Guardar coeficiente</button>
            </form>
            <p className="muted flush">El pago se registra en la pestaña Impuestos y cuotas. La pérdida fiscal y la PTU pagada no se restan: confírmalo con tu contador.</p>
          </>
        )}
      </div>

      <div className="card stack">
        <h2 className="flush">Cierre del ejercicio</h2>
        <p className="muted flush">
          {closed ? `Cerrado hasta el ${fmtDate(closed)}. No se aceptan pólizas con fecha igual o anterior.` : 'Ningún ejercicio cerrado.'}
          {' '}El cierre lleva ingresos y gastos a la cuenta 305 Resultado del ejercicio.
        </p>
        <div className="actions">
          <button className="btn" disabled={closeYear >= now.getFullYear()}
            onClick={() => confirm(`¿Cerrar el ejercicio ${closeYear}? Se genera la póliza de cierre y el periodo queda bloqueado.`) && run(() => api(`/accounting/close/${closeYear}`, { method: 'POST' }), `Ejercicio ${closeYear} cerrado.`)}>
            Cerrar ejercicio {closeYear}
          </button>
          {closed && (
            <button className="btn secondary"
              onClick={() => confirm(`¿Reabrir el ejercicio ${closed.slice(0, 4)}? Se elimina su póliza de cierre.`) && run(() => api(`/accounting/close/${closed.slice(0, 4)}`, { method: 'DELETE' }), 'Ejercicio reabierto.')}>
              Reabrir {closed.slice(0, 4)}
            </button>
          )}
        </div>
      </div>
      {msg.error && <p className="error" role="alert">{msg.error}</p>}
      {msg.ok && <p className="muted flush" role="status">{msg.ok}</p>}
    </div>
  )
}
