import { StrictMode, useEffect, useState } from 'react'
import { createRoot } from 'react-dom/client'
import type { FormEvent } from 'react'
import './style.css'

type Item = { sku: string; name: string; quantity: number; reorderPoint: number; version: number }
type Movement = { id: number; sku: string; kind: string; delta: number; balance: number; reference: string; note: string; createdAt: string }
type ReceiptResult = { reference: string; createdAt: string; replayed: boolean }
type Line = { sku: string; quantity: string }

async function api<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response
  try {
    response = await fetch(`/api/${path}`, {
      ...init,
      signal: init?.signal ?? AbortSignal.timeout(10_000),
      headers: { 'Content-Type': 'application/json', 'X-Inventory-Client': 'stockroom', ...init?.headers },
    })
  } catch (error) {
    if (init?.signal?.aborted) throw error
    throw new Error(init?.method === 'POST'
      ? 'The save could not be confirmed. Refresh stock and review the history before retrying.'
      : 'Cannot reach the stock service. Check that the API is running, then refresh stock.')
  }
  if (!response.ok) {
    const problem = await response.json().catch(() => null)
    throw new Error(problem?.detail ?? `The request failed (${response.status}). Refresh stock and try again.`)
  }
  return response.status === 204 ? undefined as T : response.json()
}

const message = (error: unknown) => error instanceof Error ? error.message : 'The operation failed. Refresh stock and try again.'
const signed = (value: number) => value > 0 ? `+${value}` : String(value)
const time = (value: string) => new Intl.DateTimeFormat(undefined, { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' }).format(new Date(value))

function App() {
  const [items, setItems] = useState<Item[]>([])
  const [selectedSku, setSelectedSku] = useState('HW-104')
  const [search, setSearch] = useState('')
  const [movements, setMovements] = useState<Movement[]>([])
  const [revision, setRevision] = useState(0)
  const [loading, setLoading] = useState(true)
  const [historyLoading, setHistoryLoading] = useState(true)
  const [itemsError, setItemsError] = useState('')
  const [historyError, setHistoryError] = useState('')
  const [mode, setMode] = useState<'receive' | 'adjust'>('receive')
  const [reference, setReference] = useState('')
  const [lines, setLines] = useState<Line[]>([{ sku: 'HW-104', quantity: '' }])
  const [delta, setDelta] = useState('')
  const [reason, setReason] = useState('')
  const [saving, setSaving] = useState(false)
  const [saveError, setSaveError] = useState('')
  const [success, setSuccess] = useState('')
  const selected = items.find(item => item.sku === selectedSku)

  async function refresh() {
    setLoading(true)
    setItemsError('')
    try { setItems(await api<Item[]>('items')) }
    catch (error) { setItemsError(message(error)) }
    finally { setLoading(false); setRevision(value => value + 1) }
  }

  useEffect(() => {
    const controller = new AbortController()
    api<Item[]>('items', { signal: controller.signal })
      .then(setItems)
      .catch(error => { if (!controller.signal.aborted) setItemsError(message(error)) })
      .finally(() => { if (!controller.signal.aborted) setLoading(false) })
    return () => controller.abort()
  }, [])

  useEffect(() => {
    const controller = new AbortController()
    setHistoryLoading(true)
    setHistoryError('')
    api<Movement[]>(`movements?sku=${encodeURIComponent(selectedSku)}`, { signal: controller.signal })
      .then(setMovements)
      .catch(error => { if (!controller.signal.aborted) setHistoryError(message(error)) })
      .finally(() => { if (!controller.signal.aborted) setHistoryLoading(false) })
    return () => controller.abort()
  }, [selectedSku, revision])

  async function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setSaving(true); setSaveError(''); setSuccess('')
    try {
      if (mode === 'receive') {
        const result = await api<ReceiptResult>('receipts', {
          method: 'POST', body: JSON.stringify({ reference, lines: lines.map(line => ({ sku: line.sku, quantity: Number(line.quantity) })) }),
        })
        setSuccess(result.replayed ? `Receipt ${result.reference} was already received. Stock was not added again.` : `Receipt ${result.reference} recorded.`)
      } else if (selected) {
        await api<void>('adjustments', { method: 'POST', body: JSON.stringify({ sku: selectedSku, delta: Number(delta), expectedVersion: selected.version, reason }) })
        setSuccess('Stock adjustment recorded. The original history is preserved.')
        setDelta(''); setReason('')
      }
      await refresh()
    } catch (error) { setSaveError(message(error)) }
    finally { setSaving(false) }
  }

  function changeMode(next: 'receive' | 'adjust') {
    setMode(next); setSaveError(''); setSuccess('')
  }

  const visible = items.filter(item => `${item.sku} ${item.name}`.toLowerCase().includes(search.toLowerCase()))
  const lowCount = items.filter(item => item.quantity <= item.reorderPoint).length
  return <>
    <a className="skip-link" href="#main">Skip to receiving desk</a>
    <header className="topbar">
      <div className="brand"><span className="shelf-mark" aria-hidden="true"><i /><i /><i /></span><span>Stockroom</span></div>
      <div className="demo-label"><span className="demo-dot" />Local demonstration<span className="demo-detail">One location · Invented data</span></div>
    </header>
    <main id="main">
      <div className="page-heading">
        <div><h1>Receiving desk</h1><p>Keep stock accurate. Keep the history.</p></div>
        <button className="button secondary" onClick={refresh} disabled={loading || saving}>{loading ? 'Refreshing…' : 'Refresh stock'}</button>
      </div>
      {itemsError && <div className="notice error" role="alert">{itemsError}</div>}
      <div className="workspace">
          <section className="panel inventory" aria-labelledby="inventory-heading" aria-busy={loading}>
            <div className="panel-heading"><div><h2 id="inventory-heading">On the shelves</h2><p>{items.length} items{lowCount > 0 && ` · ${lowCount} at or below reorder point`}</p></div></div>
            <label className="search-label">Find an item<input type="search" placeholder="Search name or item code" value={search} onChange={event => setSearch(event.target.value)} /></label>
            <div className="table-scroll"><table className="stock-table">
              <caption className="sr-only">Stock levels. Select an item to inspect its movement history.</caption>
              <thead><tr><th scope="col">Item</th><th scope="col" className="number">On hand</th><th scope="col" className="number">Reorder at</th></tr></thead>
              <tbody>{visible.map(item => <tr key={item.sku} className={selectedSku === item.sku ? 'selected' : ''}>
                <td><button className="item-button" aria-pressed={selectedSku === item.sku} onClick={() => setSelectedSku(item.sku)}><span className="item-name">{item.name}</span><span className="item-code">{item.sku}</span></button></td>
                <td className="number"><span className={item.quantity <= item.reorderPoint ? 'quantity low' : 'quantity'}>{item.quantity}</span>{item.quantity <= item.reorderPoint && <span className="low-label">Low stock</span>}</td>
                <td className="number muted">{item.reorderPoint}</td>
              </tr>)}</tbody>
            </table></div>
            {!loading && !itemsError && !visible.length && <p className="empty">No items match. Try a different name or item code.</p>}
            <p className="panel-foot">Select an item to see what changed and why.</p>
          </section>

        <section className="panel entry" aria-labelledby="entry-heading">
          <div className="entry-heading"><span className="entry-symbol" aria-hidden="true">↓</span><div><h2 id="entry-heading">Record a stock change</h2><p>Every change leaves a clear record.</p></div></div>
          <div className="mode-switch" aria-label="Stock change type"><button aria-pressed={mode === 'receive'} onClick={() => changeMode('receive')} disabled={saving}>Receive stock</button><button aria-pressed={mode === 'adjust'} onClick={() => changeMode('adjust')} disabled={saving}>Adjust stock</button></div>
          <form onSubmit={save}>
            <fieldset disabled={saving || loading || items.length === 0}>
              <legend className="sr-only">{mode === 'receive' ? 'New receipt' : 'Stock adjustment'}</legend>
              {mode === 'receive' ? <>
                <label>Receipt reference<input required maxLength={80} value={reference} onChange={event => setReference(event.target.value)} placeholder="e.g. DELIVERY-1048" aria-describedby="receipt-help" /></label>
                <p className="field-help" id="receipt-help">Use the delivery's unique reference. Receiving it again won't add stock twice.</p>
                <div className="receipt-lines">{lines.map((line, index) => <div className="receipt-line" key={index}>
                  <label>Item {index + 1}<select aria-label={`Receipt item ${index + 1}`} value={line.sku} onChange={event => setLines(values => values.map((value, i) => i === index ? { ...value, sku: event.target.value } : value))}>{items.map(item => <option key={item.sku} value={item.sku}>{item.name} ({item.sku})</option>)}</select></label>
                  <div className="quantity-row"><label>Quantity<input aria-label={`Receipt quantity ${index + 1}`} type="number" required min="1" max="100000" step="1" inputMode="numeric" placeholder="0" value={line.quantity} onChange={event => setLines(values => values.map((value, i) => i === index ? { ...value, quantity: event.target.value } : value))} /></label>{lines.length > 1 && <button type="button" className="remove-line" aria-label={`Remove receipt item ${index + 1}`} onClick={() => setLines(values => values.filter((_, i) => i !== index))}>Remove</button>}</div>
                </div>)}</div>
                {lines.length < 20 && <button type="button" className="text-link add-line" onClick={() => setLines(values => [...values, { sku: items.find(item => !values.some(line => line.sku === item.sku))?.sku ?? items[0].sku, quantity: '' }])}>+ Add another item</button>}
              </> : <>
                <label>Item to adjust<select value={selectedSku} onChange={event => setSelectedSku(event.target.value)}>{items.map(item => <option key={item.sku} value={item.sku}>{item.name}</option>)}</select></label>
                <div className="balance-preview"><span>Currently on hand</span><strong>{selected?.quantity ?? '—'} <small>units</small></strong></div>
                <label>Quantity change<input type="number" required min="-100000" max="100000" step="1" value={delta} onChange={event => setDelta(event.target.value)} placeholder="e.g. -2 or 5" aria-describedby="adjustment-help" /></label>
                <p className="field-help" id="adjustment-help">Enter the difference, not the final balance. Use a negative number to remove stock.</p>
                <label>Reason<textarea required maxLength={240} rows={3} value={reason} onChange={event => setReason(event.target.value)} placeholder="e.g. Two handles damaged during handling" /></label>
              </>}
              <button className="button primary save-button" type="submit">{saving ? 'Saving…' : mode === 'receive' ? 'Record receipt' : 'Record adjustment'}</button>
            </fieldset>
          </form>
          {saveError && <div className="notice error" role="alert">{saveError}</div>}
          {success && <div className="notice success" role="status">{success}</div>}
          <div className="entry-note"><strong>{mode === 'receive' ? 'One delivery, one receipt' : 'Keep the original record'}</strong><p>{mode === 'receive' ? 'All items are recorded together. If any line fails, the entire receipt stays unchanged.' : 'Corrections add a movement. Previous receipts and adjustments remain in the history.'}</p></div>
        </section>
        <section className="panel history" aria-labelledby="history-heading" aria-busy={historyLoading}>
          <div className="panel-heading history-heading"><div><h2 id="history-heading">Movement history</h2><p>{selected?.name ?? selectedSku}</p></div><a className="text-link" href={`/api/movements/export?sku=${encodeURIComponent(selectedSku)}`}>Export CSV</a></div>
          {historyError ? <div className="notice error" role="alert">{historyError}</div> : historyLoading ? <p className="empty">Loading movement history…</p> : movements.length === 0 ? <p className="empty">No movements recorded for this item.</p> : <div className="table-scroll" tabIndex={0} role="region" aria-label="Scrollable movement history"><table className="history-table">
            <caption className="sr-only">Latest 100 movements for {selected?.name ?? selectedSku}, newest first.</caption>
            <thead><tr><th scope="col">When / type</th><th scope="col" className="number">Change</th><th scope="col" className="number">Balance</th><th scope="col">Reference / reason</th></tr></thead>
            <tbody>{movements.map(movement => <tr key={movement.id}>
              <td><time dateTime={movement.createdAt}>{time(movement.createdAt)}</time><span className="cell-detail">{movement.kind}</span></td>
              <td className={`number movement-delta ${movement.delta < 0 ? 'negative' : ''}`}>{signed(movement.delta)}</td>
              <td className="number">{movement.balance}</td><td className="movement-note">{movement.reference || movement.note}{movement.reference && movement.note && <span className="cell-detail">{movement.note}</span>}</td>
            </tr>)}</tbody>
          </table></div>}
          <p className="panel-foot">Latest 100 movements shown. CSV includes the full history for this item.</p>
        </section>
      </div>
      <footer>Portfolio example by Patrick Schaller. Local use with invented data.</footer>
    </main>
  </>
}

createRoot(document.getElementById('root')!).render(<StrictMode><App /></StrictMode>)
