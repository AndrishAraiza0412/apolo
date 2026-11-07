import { useEffect, useState } from 'react'
import { api } from './api'

export type Sat = { regimes: Record<string, string>; uses: Record<string, string>; cancelReasons: Record<string, string> }

let cache: Promise<Sat> | null = null

// Catálogos del SAT (régimen, uso de CFDI, motivos de cancelación); se piden una vez por sesión.
export function useSat() {
  const [sat, setSat] = useState<Sat | null>(null)
  useEffect(() => { (cache ??= api<Sat>('/cfdi/catalogs')).then(setSat) }, [])
  return sat
}
