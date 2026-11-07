import { useEffect, useState } from 'react'
import { api, errorText } from './api'
import { downloadCsv, type Column, type Table } from './csv'
import Jobs from './Tareas'
import { iso } from './totals'

type Report = { code: string; name: string; description: string; period: boolean; module: string }

const now = new Date()

export default function Reportes({ currency }: { currency: string }) {
  const money = new Intl.NumberFormat('es-MX', { style: 'currency', currency })
  const num = new Intl.NumberFormat('es-MX', { maximumFractionDigits: 2 })
  const [reports, setReports] = useState<Report[]>([])
  const [code, setCode] = useState('')
  const [range, setRange] = useState({ from: iso(new Date(now.getFullYear(), 0, 1)), to: iso(now) })
  const [table, setTable] = useState<Table | null>(null)
  const [error, setError] = useState('')
  const [jobs, setJobs] = useState(0)

  useEffect(() => { api<Report[]>('/reports').then(r => { setReports(r); setCode(c => c || r[0]?.code || '') }) }, [])
  const report = reports.find(r => r.code === code)

  useEffect(() => {
    if (!code) return
    setError('')
    api<Table>(`/reports/${code}?from=${range.from}&to=${range.to}`).then(setTable).catch(err => { setTable(null); setError(errorText(err)) })
  }, [code, range])

  const fmt = (c: Column, v: string | number | null) => v === null ? '' : c.type === 'money' ? money.format(Number(v)) : c.type === 'number' ? num.format(Number(v)) : String(v)
  const totals = table && table.rows.length > 1 && table.columns.some(c => c.type === 'money') && code !== 'estado-resultados'

  // Para periodos largos: el reporte se genera en segundo plano y se descarga cuando termina.
  async function background() {
    setError('')
    try { await api('/jobs', { method: 'POST', body: { kind: 'reporte', report: code, from: range.from, to: range.to } }); setJobs(n => n + 1) }
    catch (err) { setError(errorText(err)) }
  }

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Reportes</h1>
          <div className="muted">{report?.description ?? 'Solo ves los reportes de los módulos de tu rol.'}</div>
        </div>
        <div className="actions flush">
          <button className="btn secondary" onClick={background} disabled={!code}>Generar en segundo plano</button>
          <button className="btn secondary" onClick={() => table && downloadCsv(table, `${code}-${range.to}`)} disabled={!table?.rows.length}>Descargar CSV</button>
        </div>
      </div>

      <div className="report-bar">
        <div className="chips" role="tablist" aria-label="Reportes disponibles">
          {reports.map(r => (
            <button key={r.code} role="tab" aria-selected={code === r.code} className="chip" onClick={() => setCode(r.code)}>{r.name}</button>
          ))}
        </div>
        {report?.period && (
          <div className="period">
            <label>Desde<input type="date" value={range.from} max={range.to} onChange={e => setRange(r => ({ ...r, from: e.target.value }))} /></label>
            <label>Hasta<input type="date" value={range.to} min={range.from} onChange={e => setRange(r => ({ ...r, to: e.target.value }))} /></label>
          </div>
        )}
      </div>

      {error && <p className="error" role="alert">{error}</p>}
      {!reports.length && <div className="card muted">Tu rol no tiene reportes disponibles.</div>}
      {table && (
        <div className="card">
          <table>
            <thead><tr>{table.columns.map(c => <th key={c.key} className={c.type === 'text' ? '' : 'num'}>{c.label}</th>)}</tr></thead>
            <tbody>
              {table.rows.map((r, i) => (
                <tr key={`${code}-${i}`} className="row-in" style={{ animationDelay: `${Math.min(i, 20) * 30}ms` }}>
                  {table.columns.map(c => <td key={c.key} className={c.type === 'text' ? '' : 'num'}>{fmt(c, r[c.key])}</td>)}
                </tr>
              ))}
              {!table.rows.length && <tr><td colSpan={table.columns.length} className="empty">Sin datos en el periodo.</td></tr>}
              {totals && (
                <tr className="total-row">
                  {table.columns.map((c, i) => (
                    <td key={c.key} className={c.type === 'text' ? '' : 'num'}>
                      {i === 0 ? 'Total' : c.type === 'money' ? money.format(table.rows.reduce((s, r) => s + Number(r[c.key] ?? 0), 0)) : ''}
                    </td>
                  ))}
                </tr>
              )}
            </tbody>
          </table>
        </div>
      )}
      {jobs > 0 && (
        <section className="card gap-above">
          <p className="section-title">Reportes en segundo plano</p>
          <Jobs kind="reporte" refresh={jobs} />
        </section>
      )}
    </>
  )
}
