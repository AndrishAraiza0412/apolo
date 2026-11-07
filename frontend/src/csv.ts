export type Column = { key: string; label: string; type: 'text' | 'money' | 'number' }
export type Table = { title: string; columns: Column[]; rows: Record<string, string | number | null>[] }

export function downloadCsv(table: Table, name: string) {
  const esc = (v: unknown) => `"${String(v ?? '').replace(/"/g, '""')}"`
  const text = [table.columns.map(c => esc(c.label)).join(','), ...table.rows.map(r => table.columns.map(c => esc(r[c.key])).join(','))].join('\n')
  const a = document.createElement('a')
  a.href = URL.createObjectURL(new Blob(['﻿' + text], { type: 'text/csv;charset=utf-8' }))
  a.download = `${name}.csv`
  a.click()
  URL.revokeObjectURL(a.href)
}
