const date = new Intl.DateTimeFormat('es-MX', { dateStyle: 'medium', timeZone: 'UTC' })
const local = new Intl.DateTimeFormat('es-MX', { dateStyle: 'medium' })
// 'AAAA-MM-DD' es fecha calendario (sin zona); un instante con hora se muestra en la hora local.
export const fmtDate = (d: string | null) => (d ? (d.length === 10 ? date : local).format(new Date(d)) : '—')
// Fecha local AAAA-MM-DD; toISOString() da la de UTC, que en México ya es "mañana" desde las 18:00.
export const iso = (d: Date) => new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 10)
export const round = (x: number) => Math.round((x + Number.EPSILON) * 100) / 100
export const RATES = [0.16, 0.08, 0]

// Mismo cálculo que el servidor (Facturacion.Totals); el servidor es la fuente de verdad.
export function totals(lines: [quantity: number, price: number, taxRate: number][]) {
  let subtotal = 0, tax = 0
  for (const [quantity, price, taxRate] of lines) {
    const amount = round(quantity * price)
    subtotal += amount
    tax += round(amount * taxRate)
  }
  return { subtotal: round(subtotal), tax: round(tax), total: round(subtotal + tax) }
}
