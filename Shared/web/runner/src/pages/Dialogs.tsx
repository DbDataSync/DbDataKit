import { useEffect } from 'react'
import { useMutation } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'

export function Modal({ label, testId, onCancel, children }: { label: string; testId: string; onCancel: () => void; children: React.ReactNode }) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onCancel() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onCancel])
  return (
    <div className="modal-backdrop" onMouseDown={(e) => { if (e.target === e.currentTarget) onCancel() }}>
      <div className="modal" role="dialog" aria-modal="true" aria-label={label} data-testid={testId}>
        <div className="card-head"><span className="card-title">{label}</span></div>
        <div className="card-body">{children}</div>
      </div>
    </div>
  )
}

export function ConfirmDialog({ label, text, go, onCancel, onDone }: { label: string; text: string; go: () => Promise<unknown>; onCancel: () => void; onDone: (answer: unknown) => void }) {
  const run = useMutation({ mutationFn: go, onSuccess: onDone })
  return (
    <Modal label={label} testId="runner-confirm-dialog" onCancel={onCancel}>
      <p>{text}</p>
      <ErrorBanner error={run.error} />
      <div className="row" style={{ gap: 8, justifyContent: 'flex-end' }}>
        <button type="button" className="btn" onClick={onCancel} data-testid="runner-confirm-cancel">Cancel</button>
        <button type="button" className="btn btn-danger" disabled={run.isPending} onClick={() => run.mutate()} data-testid="runner-confirm-go">{label}</button>
      </div>
    </Modal>
  )
}
