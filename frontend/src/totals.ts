const date = new Intl.DateTimeFormat('es-MX', { dateStyle: 'medium', timeZone: 'UTC' })
const local = new Intl.DateTimeFormat('es-MX', { dateStyle: 'medium' })
// 'YYYY-MM-DD' is a calendar date (no time zone); an instant with a time is shown in local time.
export const fmtDate = (d: string | null) => (d ? (d.length === 10 ? date : local).format(new Date(d)) : '—')
// Local YYYY-MM-DD date; toISOString() gives the UTC one, which in Mexico is already "tomorrow" from 18:00.
export const iso = (d: Date) => new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 10)
export const round = (x: number) => Math.round((x + Number.EPSILON) * 100) / 100
export const RATES = [0.16, 0.08, 0]

// Same calculation as the server (Facturacion.Totals); the server is the source of truth.
export function totals(lines: [quantity: number, price: number, taxRate: number][]) {
  let subtotal = 0, tax = 0
  for (const [quantity, price, taxRate] of lines) {
    const amount = round(quantity * price)
    subtotal += amount
    tax += round(amount * taxRate)
  }
  return { subtotal: round(subtotal), tax: round(tax), total: round(subtotal + tax) }
}
