// Barra con 4px redondeados arriba y base recta sobre el eje.
export function barPath(x: number, y: number, w: number, h: number) {
  const r = Math.min(4, w / 2, h)
  return `M${x},${y + h}V${y + r}Q${x},${y} ${x + r},${y}H${x + w - r}Q${x + w},${y} ${x + w},${y + r}V${y + h}Z`
}

export function niceStep(raw: number) {
  const p = 10 ** Math.floor(Math.log10(raw || 1))
  return [1, 2, 2.5, 5, 10].map(m => m * p).find(s => s >= raw) ?? 10 * p
}
