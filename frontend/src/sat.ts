import { useEffect, useState } from 'react'
import { api } from './api'

export type Sat = { regimes: Record<string, string>; uses: Record<string, string>; cancelReasons: Record<string, string> }

let cache: Promise<Sat> | null = null

// SAT catalogs (regime, CFDI use, cancellation reasons); requested once per session.
export function useSat() {
  const [sat, setSat] = useState<Sat | null>(null)
  useEffect(() => { (cache ??= api<Sat>('/cfdi/catalogs')).then(setSat) }, [])
  return sat
}
