import { useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { api, type Me } from './api'
import { Queue } from './Ia'
import { barPath, niceStep } from './chart'

type Data = {
  billing: {
    monthSales: number; paid: number; current: number; overdue: number; overdueCount: number; drafts: number
    monthly: { month: string; amount: number }[]
  } | null
  sales: { pipeline: number; openCount: number; closingSoon: { title: string; amount: number; expectedClose: string }[] } | null
  inventory: { lowCount: number; low: { sku: string; name: string; stock: number; minStock: number }[] } | null
  purchases: { pendingCount: number; pendingAmount: number; drafts: number } | null
  agent: { pending: { id: string; kind: string; summary: string; createdAt: string }[] } | null
  cash: { monthly: { month: string; balance: number }[]; forecast: { month: string; balance: number }[] } | null
}
type Task = { text: string; to: string; action: string }

const monthName = new Intl.DateTimeFormat('es-MX', { month: 'short', timeZone: 'UTC' })
const longDate = new Intl.DateTimeFormat('es-MX', { dateStyle: 'full' })

export default function Dashboard({ me }: { me: Me }) {
  const money = new Intl.NumberFormat('es-MX', { style: 'currency', currency: me.company.currency, maximumFractionDigits: 0 })
  const compact = new Intl.NumberFormat('es-MX', { notation: 'compact', maximumFractionDigits: 1 })
  const [d, setD] = useState<Data | null>(null)
  const navigate = useNavigate()
  useEffect(() => { api<Data>('/dashboard').then(setD) }, [])
  if (!d) return null

  const { billing: b, sales: s, inventory: inv, purchases: pu, agent } = d
  const tasks: Task[] = []
  if (b?.overdueCount) tasks.push({ text: `${b.overdueCount} ${b.overdueCount === 1 ? 'factura vencida' : 'facturas vencidas'} por ${money.format(b.overdue)}.`, to: '/facturacion', action: 'Ver facturas' })
  if (b?.drafts) tasks.push({ text: `${b.drafts} ${b.drafts === 1 ? 'borrador' : 'borradores'} de factura sin emitir.`, to: '/facturacion', action: 'Revisar' })
  if (inv?.lowCount) tasks.push({ text: `${inv.lowCount} productos bajo mínimo: ${inv.low.map(p => p.sku).join(', ')}.`, to: pu ? '/compras' : '/inventario', action: pu ? 'Pedir' : 'Ver inventario' })
  if (pu?.pendingCount) tasks.push({ text: `${pu.pendingCount} ${pu.pendingCount === 1 ? 'orden de compra' : 'órdenes de compra'} por recibir (${money.format(pu.pendingAmount)}).`, to: '/compras', action: 'Ver compras' })
  if (pu?.drafts) tasks.push({ text: `${pu.drafts} ${pu.drafts === 1 ? 'orden de compra' : 'órdenes de compra'} en borrador.`, to: '/compras', action: 'Revisar' })
  if (s?.closingSoon.length) tasks.push({ text: `${s.closingSoon.length} oportunidades cierran esta semana: ${s.closingSoon.map(o => o.title).join(', ')}.`, to: '/ventas', action: 'Ver pipeline' })

  const kpis = [
    b && { label: 'Ventas del mes', value: money.format(b.monthSales), note: 'sin IVA' },
    b && { label: 'Por cobrar', value: money.format(b.current + b.overdue), note: `${money.format(b.overdue)} vencido` },
    s && { label: 'Pipeline abierto', value: money.format(s.pipeline), note: `${s.openCount} oportunidades` },
    inv && { label: 'Bajo mínimo', value: String(inv.lowCount), note: 'productos' },
  ].filter(Boolean) as { label: string; value: string; note: string }[]

  return (
    <div className="stack">
      <div className="page-head flush">
        <div>
          <h1>Hola, {me.name.split(' ')[0]}</h1>
          <div className="muted">{longDate.format(new Date())} · {me.company.name}</div>
        </div>
      </div>

      <section className="card">
        <p className="section-title">Pendientes de hoy</p>
        {tasks.length ? (
          <ul className="briefing">
            {tasks.map(t => (
              <li key={t.text}>
                <span>{t.text}</span>
                <button className="btn secondary" onClick={() => navigate(t.to)}>{t.action}</button>
              </li>
            ))}
          </ul>
        ) : <p className="muted">Nada pendiente en tus módulos.</p>}
      </section>

      {kpis.length > 0 && (
        <div className="kpi-grid">
          {kpis.map((k, i) => (
            <div key={k.label} className="card kpi-tile" style={{ animationDelay: `${i * 70}ms` }}>
              <div className="muted">{k.label}</div>
              <div className="kpi">{k.value}</div>
              <div className="muted">{k.note}</div>
            </div>
          ))}
        </div>
      )}

      {d.cash && (
        <section className="card">
          <p className="section-title">Flujo de efectivo · saldo de caja y bancos al cierre de mes</p>
          <CashLine history={d.cash.monthly} forecast={d.cash.forecast} money={money} compact={compact} />
        </section>
      )}

      {agent && agent.pending.length > 0 && (
        <section>
          <p className="section-title">Cola del agente · {agent.pending.length} por aprobar</p>
          <Queue onChanged={() => api<Data>('/dashboard').then(setD)} />
        </section>
      )}

      {b && (
        <div className="chart-grid">
          <section className="card">
            <p className="section-title">Ventas por mes</p>
            <SalesBars data={b.monthly} money={money} compact={compact} />
          </section>
          <section className="card">
            <p className="section-title">Cobranza</p>
            <Donut money={money} parts={[
              { label: 'Cobrado', value: b.paid, cls: 'seg-paid' },
              { label: 'Al corriente', value: b.current, cls: 'seg-current' },
              { label: 'Vencido', value: b.overdue, cls: 'seg-overdue' },
            ]} />
          </section>
        </div>
      )}
    </div>
  )
}

function SalesBars({ data, money, compact }: { data: { month: string; amount: number }[]; money: Intl.NumberFormat; compact: Intl.NumberFormat }) {
  const [hover, setHover] = useState<number | null>(null)
  const W = 520, H = 220, L = 44, B = 24, T = 12
  const max = Math.max(...data.map(x => x.amount), 1)
  const step = niceStep(max / 3)
  const top = Math.ceil(max / step) * step
  const ticks = Array.from({ length: Math.round(top / step) + 1 }, (_, i) => i * step)
  const y = (v: number) => H - B - (v / top) * (H - B - T)
  const slot = (W - L) / data.length
  const bw = Math.min(36, slot * 0.5)
  const label = (m: string) => monthName.format(new Date(`${m}-01`))

  return (
    <div className="chart">
      <svg viewBox={`0 0 ${W} ${H}`} role="img" aria-label="Ventas por mes, últimos 6 meses">
        {ticks.map(t => (
          <g key={t}>
            <line x1={L} x2={W} y1={y(t)} y2={y(t)} className="grid" />
            <text x={L - 8} y={y(t)} className="axis" textAnchor="end" dominantBaseline="middle">{compact.format(t)}</text>
          </g>
        ))}
        {data.map((x, i) => {
          const cx = L + slot * i + slot / 2
          const h = H - B - y(x.amount)
          return (
            <g key={x.month} onMouseEnter={() => setHover(i)} onMouseLeave={() => setHover(null)}>
              <rect x={cx - slot / 2} y={T} width={slot} height={H - B - T} fill="transparent" />
              {h > 0 && <path d={barPath(cx - bw / 2, y(x.amount), bw, h)} className={`bar ${hover === i ? 'hot' : ''}`} style={{ animationDelay: `${i * 40}ms` }} />}
              <text x={cx} y={H - 6} className="axis" textAnchor="middle">{label(x.month)}</text>
            </g>
          )
        })}
      </svg>
      {hover !== null && (
        <div className="tooltip" style={{ left: `${((L + slot * hover + slot / 2) / W) * 100}%`, top: `${(y(data[hover].amount) / H) * 100}%` }}>
          <strong>{money.format(data[hover].amount)}</strong>
          <span className="muted"> · {label(data[hover].month)}</span>
        </div>
      )}
      <details className="data-table">
        <summary>Ver datos</summary>
        <table>
          <tbody>{data.map(x => <tr key={x.month}><td>{label(x.month)}</td><td className="num">{money.format(x.amount)}</td></tr>)}</tbody>
        </table>
      </details>
    </div>
  )
}

function Donut({ parts, money }: { parts: { label: string; value: number; cls: string }[]; money: Intl.NumberFormat }) {
  const [hover, setHover] = useState<number | null>(null)
  const total = parts.reduce((s, p) => s + p.value, 0)
  const r = 70, c = 2 * Math.PI * r, gap = total ? 2 : 0
  let offset = 0
  const pct = (v: number) => (total ? Math.round((v / total) * 100) : 0)
  const focus = hover === null ? null : parts[hover]

  return (
    <div className="donut">
      <div className="donut-figure">
        <svg viewBox="0 0 180 180" role="img" aria-label="Composición de la cobranza">
          <circle cx="90" cy="90" r={r} className="donut-track" />
          <g className="donut-spin">
            {parts.map((p, i) => {
              const len = total ? (p.value / total) * c : 0
              const seg = (
                <circle key={p.label} cx="90" cy="90" r={r} className={`seg ${p.cls} ${hover === i ? 'hot' : ''}`}
                  strokeDasharray={`${Math.max(len - gap, 0)} ${c}`} strokeDashoffset={-offset}
                  onMouseEnter={() => setHover(i)} onMouseLeave={() => setHover(null)} />
              )
              offset += len
              return seg
            })}
          </g>
        </svg>
        <div className="donut-center">
          <div className="muted">{focus?.label ?? 'Facturado'}</div>
          <strong>{money.format(focus?.value ?? total)}</strong>
        </div>
      </div>
      <ul className="legend">
        {parts.map((p, i) => (
          <li key={p.label} onMouseEnter={() => setHover(i)} onMouseLeave={() => setHover(null)}>
            <span className={`swatch ${p.cls}`} />
            <span>{p.label}</span>
            <span className="num">{money.format(p.value)}</span>
            <span className="muted num">{pct(p.value)}%</span>
          </li>
        ))}
      </ul>
    </div>
  )
}

// Línea de 2px que se dibuja al cargar (1.6 s, DESIGN.md §6); la proyección sigue punteada desde el último mes real.
function CashLine({ history, forecast, money, compact }: {
  history: { month: string; balance: number }[]; forecast: { month: string; balance: number }[]; money: Intl.NumberFormat; compact: Intl.NumberFormat
}) {
  const [hover, setHover] = useState<number | null>(null)
  const last = history[history.length - 1]?.month ?? ''
  const data = [...history.map(p => ({ ...p, projected: false })), ...forecast.filter(p => p.month > last).map(p => ({ ...p, projected: true }))]
  const W = 860, H = 200, L = 52, B = 24, T = 12, R = 12
  const values = data.map(x => x.balance)
  const lo = Math.min(0, ...values), hi = Math.max(1, ...values)
  const step = niceStep((hi - lo) / 3)
  const bottom = Math.floor(lo / step) * step, top = Math.ceil(hi / step) * step
  const ticks = Array.from({ length: Math.round((top - bottom) / step) + 1 }, (_, i) => bottom + i * step)
  const x = (i: number) => L + ((W - L - R) * i) / Math.max(data.length - 1, 1)
  const y = (v: number) => H - B - ((v - bottom) / (top - bottom || 1)) * (H - B - T)
  const label = (m: string) => monthName.format(new Date(`${m}-01`))
  const line = (from: number, to: number) => data.slice(from, to).map((p, i) => `${i ? 'L' : 'M'}${x(from + i)},${y(p.balance)}`).join('')
  const split = history.length

  return (
    <div className="chart">
      <svg viewBox={`0 0 ${W} ${H}`} role="img" aria-label="Saldo de caja y bancos: últimos 6 meses y proyección de 3 meses" onMouseLeave={() => setHover(null)}>
        {ticks.map(t => (
          <g key={t}>
            <line x1={L} x2={W - R} y1={y(t)} y2={y(t)} className="grid" />
            <text x={L - 8} y={y(t)} className="axis" textAnchor="end" dominantBaseline="middle">{compact.format(t)}</text>
          </g>
        ))}
        <path d={line(0, split)} className="cash-line" pathLength={1} />
        {data.length > split && <path d={line(split - 1, data.length)} className="cash-forecast" />}
        {data.map((p, i) => (
          <g key={p.month} onMouseEnter={() => setHover(i)}>
            <rect x={x(i) - (W - L - R) / data.length / 2} y={T} width={(W - L - R) / data.length} height={H - B - T} fill="transparent" />
            <circle cx={x(i)} cy={y(p.balance)} r={hover === i ? 5 : 4} className={p.projected ? 'cash-dot projected' : 'cash-dot'} />
            <text x={x(i)} y={H - 6} className="axis" textAnchor="middle">{label(p.month)}</text>
          </g>
        ))}
      </svg>
      {hover !== null && (
        <div className="tooltip" style={{ left: `${(x(hover) / W) * 100}%`, top: `${(y(data[hover].balance) / H) * 100}%` }}>
          <strong>{money.format(data[hover].balance)}</strong>
          <span className="muted"> · {label(data[hover].month)}{data[hover].projected ? ' (proyectado)' : ''}</span>
        </div>
      )}
      {data.length > split && <p className="muted chart-note">Línea punteada: proyección con cobranza, pagos comprometidos y promedios recientes. Detalle en Contabilidad › Pronóstico.</p>}
      <details className="data-table">
        <summary>Ver datos</summary>
        <table><tbody>{data.map(p => <tr key={p.month}><td>{label(p.month)}{p.projected ? ' (proyectado)' : ''}</td><td className="num">{money.format(p.balance)}</td></tr>)}</tbody></table>
      </details>
    </div>
  )
}
