import { useEffect, useState } from 'react'
import { api } from './api'
import { downloadCsv, type Table } from './csv'

type Job = { id: string; kind: string; title: string; status: 'pendiente' | 'ejecutando' | 'terminada' | 'fallida'; progress: number; message: string | null; createdAt: string; finishedAt: string | null }
type Check = { name: string; ok: boolean; detail: string }
const time = new Intl.DateTimeFormat('es-MX', { dateStyle: 'short', timeStyle: 'short' })
const STATUS = { pendiente: 'En cola', ejecutando: 'Ejecutando', terminada: 'Terminada', fallida: 'Falló' }

// Tareas en segundo plano del usuario. Consulta cada segundo solo mientras alguna sigue corriendo.
export default function Jobs({ kind, refresh = 0 }: { kind?: string; refresh?: number }) {
  const [jobs, setJobs] = useState<Job[]>([])
  const [tick, setTick] = useState(0)
  const running = jobs.some(j => j.status === 'pendiente' || j.status === 'ejecutando')

  useEffect(() => { api<Job[]>('/jobs').then(js => setJobs(kind ? js.filter(j => j.kind === kind) : js)) }, [kind, refresh, tick])
  useEffect(() => {
    if (!running) return
    const t = setTimeout(() => setTick(n => n + 1), 1000)
    return () => clearTimeout(t)
  }, [running, jobs])

  if (!jobs.length) return <p className="empty">Sin tareas en segundo plano.</p>
  return <div className="jobs">{jobs.map(j => <JobCard key={j.id} j={j} />)}</div>
}

function JobCard({ j }: { j: Job }) {
  const [result, setResult] = useState<Check[] | null>(null)
  const done = j.status === 'terminada'
  useEffect(() => { if (done && j.kind === 'conciliacion') api<{ result: Check[] }>(`/jobs/${j.id}`).then(r => setResult(r.result)) }, [done, j.id, j.kind])

  async function csv() {
    const r = await api<{ result: Table }>(`/jobs/${j.id}`)
    downloadCsv(r.result, `${j.title.replace(/^Reporte: /, '')}-${j.createdAt.slice(0, 10)}`)
  }

  return (
    <div className={`job ${j.status}`}>
      <div className="job-head">
        <strong>{j.title}</strong>
        <span className={`pill ${done ? 'ok' : ''}`}>{STATUS[j.status]}</span>
      </div>
      <div className="progress-track" role="progressbar" aria-label={j.title} aria-valuemin={0} aria-valuemax={100} aria-valuenow={j.progress}>
        <div className="progress-fill" style={{ width: `${j.progress}%` }} />
      </div>
      <div className="muted">{j.progress}% · {j.message ?? 'En cola'} · {time.format(new Date(j.createdAt))}</div>
      {result && (
        <ul className="checks">
          {result.map(c => (
            <li key={c.name}>
              <span className={c.ok ? 'ok' : 'bad'} aria-label={c.ok ? 'Cuadra' : 'Diferencia'}>{c.ok ? '✓' : '!'}</span>
              <span><strong>{c.name}.</strong> {c.detail}</span>
            </li>
          ))}
        </ul>
      )}
      {done && j.kind === 'reporte' && <div><button className="btn secondary" onClick={csv}>Descargar CSV</button></div>}
    </div>
  )
}
