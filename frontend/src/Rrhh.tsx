import { useEffect, useState, type FormEvent } from 'react'
import { api, errorText } from './api'
import { fmtDate } from './totals'

type Employee = {
  id: string; name: string; rfc: string | null; position: string | null; department: string | null; hireDate: string | null
  terminationDate: string | null; dailySalary: number; active: boolean
}
type Kind = 'ordinaria' | 'aguinaldo' | 'ptu' | 'finiquito'
type RunRow = { id: string; folioText: string; kind: Kind; startDate: string; endDate: string; status: string; paidAt: string | null; gross: number; deductions: number; net: number; employerCost: number; employees: number }
type Concept = { name: string; amount: number; exempt: number; separation: boolean }
type RunLine = {
  employeeId: string; employee: string; days: number; dailySalary: number; sbc: number; doubleHours: number; tripleHours: number; overtime: number
  vacationDays: number; vacationPremium: number; absences: number; sickDays: number; exempt: number
  gross: number; isr: number; subsidy: number; imss: number; other: number; isrAdjustment: number; deductions: number; net: number; employer: number
  concepts: Concept[]
}
type Run = Omit<RunRow, 'employees'> & { cause: string | null; lines: RunLine[]; employer: { imss: number; rcv: number; infonavit: number; riskPremium: number } }

const KIND: Record<Kind, string> = { ordinaria: 'Ordinaria', aguinaldo: 'Aguinaldo', ptu: 'PTU', finiquito: 'Finiquito' }
const period = (r: { kind: Kind; startDate: string; endDate: string }) =>
  r.kind === 'ordinaria' ? `${fmtDate(r.startDate)} – ${fmtDate(r.endDate)}` : r.kind === 'finiquito' ? `Baja ${fmtDate(r.endDate)}` : `Pago ${fmtDate(r.endDate)}`

type Tab = 'empleados' | 'nomina' | 'anual' | 'parametros'

export default function Rrhh({ currency }: { currency: string }) {
  const money = new Intl.NumberFormat('es-MX', { style: 'currency', currency })
  const [tab, setTab] = useState<Tab>('empleados')
  return (
    <>
      <div className="page-head">
        <div>
          <h1>RR.HH. / Nómina</h1>
          <div className="muted">Empleados; nómina ordinaria, aguinaldo, PTU y finiquitos; cálculo anual y parámetros de nómina de la empresa.</div>
        </div>
      </div>
      <div className="tabs" role="tablist">
        {([['empleados', 'Empleados'], ['nomina', 'Nómina'], ['anual', 'Cálculo anual'], ['parametros', 'Parámetros']] as const).map(([k, l]) => (
          <button key={k} role="tab" aria-selected={tab === k} onClick={() => setTab(k)}>{l}</button>
        ))}
      </div>
      {tab === 'empleados' && <Empleados money={money} />}
      {tab === 'nomina' && <Nomina money={money} />}
      {tab === 'anual' && <Anual money={money} />}
      {tab === 'parametros' && <Parametros />}
    </>
  )
}

function Empleados({ money }: { money: Intl.NumberFormat }) {
  const [rows, setRows] = useState<Employee[]>([])
  const [editing, setEditing] = useState<Employee | 'new' | null>(null)
  const load = () => api<Employee[]>('/employees').then(setRows)
  useEffect(() => { load() }, [])
  const active = rows.filter(r => r.active)

  return (
    <div className="stack">
      <div className="page-head flush">
        <div className="muted">{active.length} activos · nómina diaria {money.format(active.reduce((s, r) => s + r.dailySalary, 0))}</div>
        <button className="btn" onClick={() => setEditing(editing ? null : 'new')}>{editing ? 'Cancelar' : 'Nuevo empleado'}</button>
      </div>
      {editing && (
        <div className="card">
          <p className="section-title">{editing === 'new' ? 'Nuevo empleado' : 'Editar empleado'}</p>
          <EmployeeForm key={editing === 'new' ? 'new' : editing.id} e={editing === 'new' ? undefined : editing} onSaved={() => { setEditing(null); load() }} />
        </div>
      )}
      <div className="card">
        <table>
          <thead><tr><th>Nombre</th><th>Puesto</th><th>Departamento</th><th>Ingreso</th><th className="num">Sueldo diario</th><th>Estado</th></tr></thead>
          <tbody>
            {rows.map((r, i) => (
              <tr key={r.id} tabIndex={0} className="clickable row-in" style={{ animationDelay: `${i * 35}ms` }}
                aria-selected={editing !== 'new' && editing?.id === r.id}
                onClick={() => setEditing(r)} onKeyDown={e => e.key === 'Enter' && setEditing(r)}>
                <td>{r.name}</td><td>{r.position ?? '—'}</td><td>{r.department ?? '—'}</td><td>{fmtDate(r.hireDate)}</td>
                <td className="num">{money.format(r.dailySalary)}</td>
                <td><span className={`pill ${r.active ? 'ok' : 'muted-pill'}`}>{r.active ? 'Activo' : r.terminationDate ? `Baja ${fmtDate(r.terminationDate)}` : 'Inactivo'}</span></td>
              </tr>
            ))}
            {!rows.length && <tr><td colSpan={6} className="empty">Sin empleados. Da de alta el primero con «Nuevo empleado».</td></tr>}
          </tbody>
        </table>
      </div>
    </div>
  )
}

function EmployeeForm({ e, onSaved }: { e?: Employee; onSaved: () => void }) {
  const [error, setError] = useState('')

  async function submit(ev: FormEvent<HTMLFormElement>) {
    ev.preventDefault()
    const f = Object.fromEntries(new FormData(ev.currentTarget)) as Record<string, string>
    const body = { ...f, dailySalary: Number(f.dailySalary), hireDate: f.hireDate || null, terminationDate: f.terminationDate || null, active: f.active !== undefined || !e }
    setError('')
    try { await api(e ? `/employees/${e.id}` : '/employees', { method: e ? 'PUT' : 'POST', body }); onSaved() } catch (err) { setError(errorText(err)) }
  }

  return (
    <form onSubmit={submit} className="form-grid">
      <label className="wide">Nombre completo<input name="name" defaultValue={e?.name} required /></label>
      <label>RFC<input name="rfc" defaultValue={e?.rfc ?? ''} maxLength={13} /></label>
      <label>Puesto<input name="position" defaultValue={e?.position ?? ''} /></label>
      <label>Departamento<input name="department" defaultValue={e?.department ?? ''} /></label>
      <label>Fecha de ingreso<input name="hireDate" type="date" defaultValue={e?.hireDate ?? ''} /></label>
      <label>Sueldo diario<input name="dailySalary" type="number" min="0.01" step="0.01" defaultValue={e?.dailySalary ?? ''} required /></label>
      {e && <label title="Se llena sola al pagar el finiquito">Fecha de baja<input name="terminationDate" type="date" defaultValue={e.terminationDate ?? ''} /></label>}
      {e && <label className="check"><input name="active" type="checkbox" defaultChecked={e.active} /> Activo</label>}
      <button className="btn">{e ? 'Guardar' : 'Crear'}</button>
      {error && <p className="error wide" role="alert">{error}</p>}
    </form>
  )
}

const HINT: Record<Kind, string> = {
  ordinaria: 'Sueldo diario × días más horas extra y prima vacacional, menos faltas e incapacidades; los activos entran con los días del periodo.',
  aguinaldo: 'Días de aguinaldo de la empresa proporcionales a los días trabajados en el año de la fecha de pago; exento hasta 30 UMA.',
  ptu: 'Se reparte 50% por días y 50% por salarios del año anterior, incluidas las bajas de ese año; tope del mayor entre los días configurados y el promedio de los últimos tres años.',
  finiquito: 'Salario pendiente, aguinaldo, vacaciones y prima proporcionales; por despido injustificado suma 3 meses, 20 días por año y prima de antigüedad. Al pagarlo se da de baja al empleado.',
}

function Nomina({ money }: { money: Intl.NumberFormat }) {
  const [rows, setRows] = useState<RunRow[]>([])
  const [employees, setEmployees] = useState<Employee[]>([])
  const [open, setOpen] = useState<string | null>(null)
  const [error, setError] = useState('')
  const [kind, setKind] = useState<Kind>('ordinaria')
  const load = () => api<RunRow[]>('/payroll').then(setRows)
  useEffect(() => { load(); api<Employee[]>('/employees').then(setEmployees) }, [])

  async function create(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const f = Object.fromEntries(new FormData(e.currentTarget)) as Record<string, string>
    const body = {
      kind, startDate: f.startDate ?? f.endDate, endDate: f.endDate, ptuAmount: f.ptuAmount ? Number(f.ptuAmount) : null,
      employeeId: f.employeeId || null, cause: f.cause || null,
    }
    setError('')
    try { const { id } = await api<{ id: string }>('/payroll', { method: 'POST', body }); await load(); setOpen(id) } catch (err) { setError(errorText(err)) }
  }

  return (
    <div className="stack">
      <form className="card form-grid" onSubmit={create}>
        <label>Tipo
          <select value={kind} onChange={e => setKind(e.target.value as Kind)}>
            {Object.entries(KIND).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </select>
        </label>
        {kind === 'ordinaria' && <label>Inicio del periodo<input name="startDate" type="date" required /></label>}
        {kind === 'finiquito' && (
          <>
            <label>Empleado
              <select name="employeeId" required defaultValue="">
                <option value="" disabled>Elige…</option>
                {employees.filter(x => x.active).map(x => <option key={x.id} value={x.id}>{x.name}</option>)}
              </select>
            </label>
            <label>Causa
              <select name="cause" defaultValue="renuncia"><option value="renuncia">Renuncia</option><option value="despido">Despido injustificado</option></select>
            </label>
          </>
        )}
        <label>{kind === 'ordinaria' ? 'Fin del periodo' : kind === 'finiquito' ? 'Fecha de baja' : 'Fecha de pago'}<input name="endDate" type="date" required /></label>
        {kind === 'ptu' && <label>Monto a repartir<input name="ptuAmount" type="number" min="0.01" step="0.01" required placeholder="10% de la renta gravable" /></label>}
        <button className="btn">{kind === 'finiquito' ? 'Calcular finiquito' : 'Nueva nómina'}</button>
        <p className="muted wide flush">{HINT[kind]}</p>
        {error && <p className="error wide" role="alert">{error}</p>}
      </form>
      {open && <RunEditor key={open} id={open} money={money} onClose={() => setOpen(null)} onChanged={() => load()} onDeleted={() => { setOpen(null); load() }} />}
      <div className="card">
        <table>
          <thead><tr><th>Folio</th><th>Tipo</th><th>Periodo</th><th className="num">Empleados</th><th className="num">Bruto</th><th className="num">Deducciones</th><th className="num">Neto</th><th className="num">Costo patronal</th><th>Estado</th></tr></thead>
          <tbody>
            {rows.map((r, i) => (
              <tr key={r.id} tabIndex={0} className="clickable row-in" style={{ animationDelay: `${i * 35}ms` }} aria-selected={open === r.id}
                onClick={() => setOpen(r.id)} onKeyDown={e => e.key === 'Enter' && setOpen(r.id)}>
                <td>{r.folioText}</td><td>{KIND[r.kind]}</td><td>{period(r)}</td><td className="num">{r.employees}</td>
                <td className="num">{money.format(r.gross)}</td><td className="num">{money.format(r.deductions)}</td><td className="num">{money.format(r.net)}</td>
                <td className="num">{r.employerCost ? money.format(r.employerCost) : '—'}</td>
                <td><span className={`pill ${r.status === 'pagada' ? 'ok' : ''}`}>{r.status === 'pagada' ? 'Pagada' : 'Borrador'}</span></td>
              </tr>
            ))}
            {!rows.length && <tr><td colSpan={9} className="empty">Sin nóminas. Crea la primera con el formulario de arriba.</td></tr>}
          </tbody>
        </table>
      </div>
    </div>
  )
}

type NumKey = 'days' | 'doubleHours' | 'tripleHours' | 'vacationDays' | 'absences' | 'sickDays' | 'other'

function RunEditor({ id, money, onClose, onChanged, onDeleted }: { id: string; money: Intl.NumberFormat; onClose: () => void; onChanged: () => void; onDeleted: () => void }) {
  const [run, setRun] = useState<Run | null>(null)
  const [lines, setLines] = useState<RunLine[]>([])
  const [error, setError] = useState('')
  const [busy, setBusy] = useState('')
  const load = () => api<Run>(`/payroll/${id}`).then(r => { setRun(r); setLines(r.lines) })
  useEffect(() => { load() }, [id]) // eslint-disable-line react-hooks/exhaustive-deps
  if (!run) return null

  const draft = run.status === 'borrador'
  const ordinary = run.kind === 'ordinaria'
  const settlement = run.kind === 'finiquito'
  const set = (i: number, patch: Partial<RunLine>) => setLines(ls => ls.map((l, j) => (j === i ? { ...l, ...patch } : l)))
  const sum = (k: 'gross' | 'exempt' | 'isr' | 'subsidy' | 'imss' | 'other' | 'isrAdjustment' | 'net' | 'employer') => lines.reduce((s, l) => s + l[k], 0)
  const adjusted = lines.some(l => l.isrAdjustment)

  async function run_(label: string, fn: () => Promise<unknown>) {
    setBusy(label); setError('')
    try { await fn(); await load(); onChanged() } catch (err) { setError(errorText(err)) }
    setBusy('')
  }
  const save = () => api(`/payroll/${id}/lines`, {
    method: 'PUT',
    body: lines.map(l => ({
      employeeId: l.employeeId, days: l.days, other: l.other, doubleHours: l.doubleHours, tripleHours: l.tripleHours,
      vacationDays: l.vacationDays, absences: l.absences, sickDays: l.sickDays,
    })),
  })
  // ISR e IMSS los calcula el servidor: al salir de un campo se guarda y se recalcula.
  const recalc = () => run_('save', save)
  const num = (i: number, k: NumKey, label: string, max?: number, step = '0.5') => (
    <td><input className="num" type="number" min="0" max={max} step={step} value={lines[i][k]} disabled={!draft}
      onChange={e => set(i, { [k]: Number(e.target.value) })} onBlur={recalc} aria-label={`${label} de ${lines[i].employee}`} /></td>
  )

  return (
    <div className="card stack">
      <div className="detail-head">
        <div>
          <p className="section-title flush">
            {settlement ? `${run.cause === 'despido' ? 'Liquidación' : 'Finiquito'} ${run.folioText}` : `Nómina ${KIND[run.kind].toLowerCase()} ${run.folioText}`} · {period(run)}
          </p>
          {run.paidAt && <div className="muted">Pagada {fmtDate(run.paidAt)}</div>}
        </div>
        <button className="icon-btn" onClick={onClose} aria-label="Cerrar nómina">×</button>
      </div>
      <div className="table-scroll">
        <table className="lines">
          <thead>
            <tr>
              <th>Empleado</th>
              <th className="num">{run.kind === 'ptu' ? 'Días en el año' : settlement ? 'Días pendientes' : 'Días'}</th>
              <th className="num">Sueldo diario</th>
              {ordinary && <>
                <th className="num">SBC</th><th className="num" title="Horas extra dobles">H. dobles</th><th className="num" title="Horas extra triples">H. triples</th>
                <th className="num" title="Días de vacaciones gozados: pagan prima vacacional">Vacaciones</th><th className="num">Faltas</th><th className="num" title="Incapacidades">Incap.</th>
              </>}
              {settlement && <th className="num" title="Días de vacaciones de años anteriores sin disfrutar">Vac. pendientes</th>}
              <th className="num">Bruto</th><th className="num">Exento</th><th className="num">ISR</th>
              {ordinary && <><th className="num">Subsidio</th><th className="num">IMSS</th></>}
              {adjusted && <th className="num" title="Diferencia del cálculo anual">Ajuste anual</th>}
              <th className="num">Otras</th><th className="num">Neto</th>
              {ordinary && <th className="num">Patronal</th>}
            </tr>
          </thead>
          <tbody>
            {lines.map((l, i) => (
              <tr key={l.employeeId}>
                <td>{l.employee}</td>
                {run.kind === 'ptu' ? <td className="num">{l.days}</td> : num(i, 'days', 'Días', ordinary ? 31 : 90, ordinary ? '0.5' : '0.01')}
                <td className="num">{money.format(l.dailySalary)}</td>
                {ordinary && <>
                  <td className="num">{money.format(l.sbc)}</td>
                  {num(i, 'doubleHours', 'Horas dobles')}{num(i, 'tripleHours', 'Horas triples')}
                  {num(i, 'vacationDays', 'Vacaciones', 31)}{num(i, 'absences', 'Faltas', 31)}{num(i, 'sickDays', 'Incapacidades', 31)}
                </>}
                {settlement && num(i, 'vacationDays', 'Vacaciones pendientes', 60)}
                <td className="num" title={[l.overtime && `Horas extra ${money.format(l.overtime)}`, l.vacationPremium && `Prima vacacional ${money.format(l.vacationPremium)}`].filter(Boolean).join(' · ') || undefined}>
                  {money.format(l.gross)}
                </td>
                <td className="num">{l.exempt ? money.format(l.exempt) : '—'}</td>
                <td className="num">{money.format(l.isr)}</td>
                {ordinary && <><td className="num">{l.subsidy ? `−${money.format(l.subsidy)}` : '—'}</td><td className="num">{money.format(l.imss)}</td></>}
                {adjusted && <td className="num">{l.isrAdjustment ? money.format(l.isrAdjustment) : '—'}</td>}
                {num(i, 'other', 'Otras deducciones', undefined, '0.01')}
                <td className="num">{money.format(l.net)}</td>
                {ordinary && <td className="num">{money.format(l.employer)}</td>}
              </tr>
            ))}
            {!settlement && (
              <tr className="total-row">
                <td colSpan={ordinary ? 9 : 3}>Totales</td>
                <td className="num">{money.format(sum('gross'))}</td><td className="num">{money.format(sum('exempt'))}</td><td className="num">{money.format(sum('isr'))}</td>
                {ordinary && <><td className="num">−{money.format(sum('subsidy'))}</td><td className="num">{money.format(sum('imss'))}</td></>}
                {adjusted && <td className="num">{money.format(sum('isrAdjustment'))}</td>}
                <td className="num">{money.format(sum('other'))}</td><td className="num"><strong>{money.format(sum('net'))}</strong></td>
                {ordinary && <td className="num">{money.format(sum('employer'))}</td>}
              </tr>
            )}
          </tbody>
        </table>
      </div>
      {settlement && lines[0] && (
        <table>
          <thead><tr><th>Concepto</th><th className="num">Importe</th><th className="num">Exento</th><th className="num">Gravado</th></tr></thead>
          <tbody>
            {lines[0].concepts.map(c => (
              <tr key={c.name}><td>{c.name}{c.separation && <span className="pill muted-pill inline">Separación</span>}</td>
                <td className="num">{money.format(c.amount)}</td><td className="num">{money.format(c.exempt)}</td><td className="num">{money.format(c.amount - c.exempt)}</td></tr>
            ))}
            <tr className="total-row"><td>Total</td><td className="num">{money.format(lines[0].gross)}</td><td className="num">{money.format(lines[0].exempt)}</td><td className="num">{money.format(lines[0].gross - lines[0].exempt)}</td></tr>
          </tbody>
        </table>
      )}
      {ordinary && (
        <dl className="kv">
          <dt>IMSS patronal</dt><dd>{money.format(run.employer.imss)}</dd>
          <dt>Retiro, cesantía y vejez</dt><dd>{money.format(run.employer.rcv)}</dd>
          <dt>INFONAVIT</dt><dd>{money.format(run.employer.infonavit)}</dd>
          <dt>Costo total para la empresa</dt><dd><strong>{money.format(run.gross + run.employerCost)}</strong></dd>
        </dl>
      )}
      <p className="muted flush">
        {ordinary && <>ISR con la tarifa del art. 96 ajustada a los días pagados (sin la parte exenta) y subsidio para el empleo; cuotas obreras y patronales sobre el SBC por los días cotizados (sin faltas ni incapacidades), prima de riesgo de {(run.employer.riskPremium * 100).toFixed(5)}%. En salario mínimo la empresa absorbe la cuota obrera.</>}
        {(run.kind === 'aguinaldo' || run.kind === 'ptu') && <>ISR de pago extraordinario: la parte gravada se suma al sueldo mensual y se toma la diferencia de la tarifa. Sin cuotas IMSS.</>}
        {settlement && <>Lo ordinario gravado se calcula como pago extraordinario; la indemnización y la prima de antigüedad gravadas, a la tasa efectiva del último sueldo mensual. Exento por separación: 90 UMA por año de servicio.</>}
        {' '}Los parámetros se editan en la pestaña Parámetros.
      </p>
      {error && <p className="error" role="alert">{error}</p>}
      {draft && (
        <div className="actions">
          <button className="btn secondary" disabled={!!busy} onClick={() => run_('save', save)}>{busy === 'save' ? 'Guardando…' : 'Guardar'}</button>
          <button className="btn" disabled={!!busy}
            onClick={() => confirm(`¿Pagar ${settlement ? 'el finiquito' : 'la nómina'} por ${money.format(run.net)}? Se genera la póliza de egreso y ya no se puede editar.${settlement ? ' El empleado queda dado de baja.' : ''}`)
              && run_('pay', async () => { await save(); await api(`/payroll/${id}/pay`, { method: 'POST' }) })}>
            {busy === 'pay' ? 'Pagando…' : settlement ? 'Pagar finiquito' : 'Pagar nómina'}
          </button>
          <button className="btn secondary" disabled={!!busy}
            onClick={async () => { if (!confirm('¿Borrar este borrador?')) return; try { await api(`/payroll/${id}`, { method: 'DELETE' }); onDeleted() } catch (err) { setError(errorText(err)) } }}>
            Borrar borrador
          </button>
        </div>
      )}
    </div>
  )
}

type AnnualRow = { employeeId: string; employee: string; eligible: boolean; reason: string | null; taxable: number; isr: number; annual: number; adjustment: number }

function Anual({ money }: { money: Intl.NumberFormat }) {
  const [year, setYear] = useState(new Date().getFullYear())
  const [rows, setRows] = useState<AnnualRow[] | null>(null)
  const [runs, setRuns] = useState<RunRow[]>([])
  const [target, setTarget] = useState('')
  const [msg, setMsg] = useState({ error: '', ok: '' })
  useEffect(() => { setRows(null); api<AnnualRow[]>(`/payroll/annual/${year}`).then(setRows) }, [year])
  useEffect(() => { api<RunRow[]>('/payroll').then(setRuns) }, [])
  const drafts = runs.filter(r => r.kind === 'ordinaria' && r.status === 'borrador' && r.endDate.startsWith(String(year)))

  async function apply() {
    setMsg({ error: '', ok: '' })
    try { await api(`/payroll/annual/${year}/apply/${target}`, { method: 'POST' }); setMsg({ error: '', ok: 'Ajuste aplicado a la nómina en borrador.' }) }
    catch (err) { setMsg({ error: errorText(err), ok: '' }) }
  }

  return (
    <div className="card stack">
      <div className="page-head flush">
        <div className="muted">Cálculo anual del ISR (art. 97 LISR) con las nóminas pagadas del año: tarifa anual sobre el ingreso gravado menos el ISR de cada nómina. Positivo se retiene; negativo se devuelve al trabajador.</div>
        <label>Año<input type="number" min="2000" max="2100" value={year} onChange={e => setYear(Number(e.target.value))} /></label>
      </div>
      <table>
        <thead><tr><th>Empleado</th><th className="num">Ingreso gravado</th><th className="num">ISR anual</th><th className="num">ISR de nóminas</th><th className="num">Diferencia</th><th>Observación</th></tr></thead>
        <tbody>
          {rows?.map(r => (
            <tr key={r.employeeId}>
              <td>{r.employee}</td><td className="num">{money.format(r.taxable)}</td><td className="num">{money.format(r.annual)}</td><td className="num">{money.format(r.isr)}</td>
              <td className="num"><strong>{r.eligible ? money.format(r.adjustment) : '—'}</strong>{r.eligible && r.adjustment < 0 && <div className="muted">A favor</div>}</td>
              <td className="muted">{r.reason ?? 'Aplica cálculo anual'}</td>
            </tr>
          ))}
          {rows && !rows.length && <tr><td colSpan={6} className="empty">Sin nóminas pagadas en {year}.</td></tr>}
          {!rows && <tr><td colSpan={6} className="empty">Calculando…</td></tr>}
        </tbody>
      </table>
      <div className="actions">
        <select value={target} onChange={e => setTarget(e.target.value)} aria-label="Nómina en borrador donde aplicar el ajuste">
          <option value="">Nómina ordinaria en borrador de {year}…</option>
          {drafts.map(r => <option key={r.id} value={r.id}>{period(r)}</option>)}
        </select>
        <button className="btn" disabled={!target || !rows?.some(r => r.eligible)} onClick={apply}>Aplicar ajuste</button>
      </div>
      {msg.error && <p className="error" role="alert">{msg.error}</p>}
      {msg.ok && <p className="muted flush" role="status">{msg.ok}</p>}
      <p className="muted flush">Confirma el criterio con tu contador antes de aplicar: el ISR de cada nómina se toma antes del subsidio.</p>
    </div>
  )
}

type Bracket = { lower: number; fixed: number; rate: number }
type Step = { upToUma: number; rate: number }
type Params = Record<string, number | boolean | Bracket[] | Step[]> & { tariff: Bracket[]; cesantia: Step[] }

// [clave, etiqueta, unidad]; '%' se captura en porcentaje.
const FIELDS: { title: string; fields: [string, string, string][] }[] = [
  { title: 'Valores oficiales', fields: [['uma', 'UMA diaria', '$'], ['minWage', 'Salario mínimo diario', '$'], ['daysPerMonth', 'Días por mes', 'días'], ['subsidyRate', 'Subsidio para el empleo', '% UMA'], ['subsidyLimit', 'Límite mensual del subsidio', '$']] },
  { title: 'Prestaciones de la empresa', fields: [['aguinaldoDays', 'Días de aguinaldo', 'días'], ['vacationPremium', 'Prima vacacional', '%'], ['extraVacationDays', 'Días extra de vacaciones', 'días'], ['hoursPerDay', 'Jornada diaria', 'h'], ['doubleFactor', 'Pago hora doble', '×'], ['tripleFactor', 'Pago hora triple', '×'], ['ptuCapDays', 'Tope de PTU', 'días']] },
  { title: 'Exenciones de ISR', fields: [['overtimeExemptShare', 'Parte exenta de horas dobles', '%'], ['overtimeExemptUmaPerWeek', 'Tope exento horas extra por semana', 'UMA'], ['aguinaldoExemptUma', 'Aguinaldo exento', 'UMA'], ['ptuExemptUma', 'PTU exenta', 'UMA'], ['vacationPremiumExemptUma', 'Prima vacacional exenta', 'UMA'], ['severanceExemptUmaPerYear', 'Separación exenta por año', 'UMA']] },
  { title: 'IMSS e INFONAVIT', fields: [['sbcCapUma', 'Tope del SBC', 'UMA'], ['workerExcessRate', 'Obrera excedente de 3 UMA', '%'], ['workerRate', 'Obrera sobre SBC', '%'], ['imssFixedRate', 'Patronal cuota fija (sobre UMA)', '%'], ['imssExcessRate', 'Patronal excedente de 3 UMA', '%'], ['imssRate', 'Patronal sobre SBC (sin riesgo)', '%'], ['riskPremium', 'Prima de riesgo de trabajo', '%'], ['retirementRate', 'Retiro', '%'], ['minWageCesantiaRate', 'Cesantía y vejez en salario mínimo', '%'], ['infonavitRate', 'INFONAVIT', '%']] },
]
const pct = (unit: string) => unit.startsWith('%')
const r6 = (x: number) => Math.round(x * 1e6) / 1e6

function Parametros() {
  const [p, setP] = useState<Params | null>(null)
  const [msg, setMsg] = useState({ error: '', ok: '' })
  const load = () => api<{ params: Params }>('/payroll/settings').then(r => setP(r.params))
  useEffect(() => { load() }, [])
  if (!p) return null

  const setField = (k: string, v: number | boolean) => setP({ ...p, [k]: v })
  async function save(e: FormEvent) {
    e.preventDefault()
    setMsg({ error: '', ok: '' })
    try { await api('/payroll/settings', { method: 'PUT', body: p }); setMsg({ error: '', ok: 'Parámetros guardados. Las nóminas en borrador se recalculan al editarlas.' }) }
    catch (err) { setMsg({ error: errorText(err), ok: '' }) }
  }
  async function reset() {
    if (!confirm('¿Volver a los valores legales vigentes? Se pierden los parámetros propios de la empresa.')) return
    await api('/payroll/settings', { method: 'DELETE' }); await load(); setMsg({ error: '', ok: 'Se restablecieron los valores legales.' })
  }

  return (
    <form className="stack" onSubmit={save}>
      {FIELDS.map(g => (
        <div key={g.title} className="card">
          <p className="section-title">{g.title}</p>
          <div className="form-grid">
            {g.fields.map(([k, label, unit]) => (
              <label key={k}>{label} ({unit})
                <input type="number" step="any" min="0" required value={pct(unit) ? r6((p[k] as number) * 100) : (p[k] as number)}
                  onChange={e => setField(k, pct(unit) ? Number(e.target.value) / 100 : Number(e.target.value))} />
              </label>
            ))}
            {g.title === 'IMSS e INFONAVIT' && (
              <label className="check"><input type="checkbox" checked={p.employerPaysMinWageQuota as boolean} onChange={e => setField('employerPaysMinWageQuota', e.target.checked)} /> La empresa cubre la cuota obrera en salario mínimo</label>
            )}
          </div>
        </div>
      ))}
      <div className="card">
        <p className="section-title">Tarifa mensual de ISR (art. 96)</p>
        <table className="lines">
          <thead><tr><th className="num">Límite inferior</th><th className="num">Cuota fija</th><th className="num">% sobre excedente</th><th /></tr></thead>
          <tbody>
            {p.tariff.map((t, i) => (
              <tr key={i}>
                {(['lower', 'fixed', 'rate'] as const).map(k => (
                  <td key={k}><input className="num" type="number" step="any" min="0" required value={k === 'rate' ? r6(t[k] * 100) : t[k]} aria-label={`${k} renglón ${i + 1}`}
                    onChange={e => setP({ ...p, tariff: p.tariff.map((x, j) => j === i ? { ...x, [k]: k === 'rate' ? Number(e.target.value) / 100 : Number(e.target.value) } : x) })} /></td>
                ))}
                <td><button type="button" className="icon-btn" aria-label="Quitar renglón" disabled={p.tariff.length === 1} onClick={() => setP({ ...p, tariff: p.tariff.filter((_, j) => j !== i) })}>×</button></td>
              </tr>
            ))}
          </tbody>
        </table>
        <button type="button" className="btn secondary gap-above" onClick={() => setP({ ...p, tariff: [...p.tariff, { lower: 0, fixed: 0, rate: 0 }] })}>Agregar renglón</button>
      </div>
      <div className="card">
        <p className="section-title">Cesantía y vejez patronal por SBC</p>
        <table className="lines">
          <thead><tr><th className="num">Hasta (veces UMA)</th><th className="num">Tasa %</th></tr></thead>
          <tbody>
            {p.cesantia.map((c, i) => (
              <tr key={i}>
                <td><input className="num" type="number" step="any" min="0" required value={c.upToUma} aria-label={`Límite renglón ${i + 1}`}
                  onChange={e => setP({ ...p, cesantia: p.cesantia.map((x, j) => j === i ? { ...x, upToUma: Number(e.target.value) } : x) })} /></td>
                <td><input className="num" type="number" step="any" min="0" required value={r6(c.rate * 100)} aria-label={`Tasa renglón ${i + 1}`}
                  onChange={e => setP({ ...p, cesantia: p.cesantia.map((x, j) => j === i ? { ...x, rate: Number(e.target.value) / 100 } : x) })} /></td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {msg.error && <p className="error" role="alert">{msg.error}</p>}
      {msg.ok && <p className="muted flush" role="status">{msg.ok}</p>}
      <div className="actions">
        <button type="button" className="btn secondary" onClick={reset}>Restablecer valores legales</button>
        <button className="btn">Guardar parámetros</button>
      </div>
    </form>
  )
}
