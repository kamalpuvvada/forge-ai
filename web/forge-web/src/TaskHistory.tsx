import type { EngineeringTaskSummary } from './types'

export function TaskHistory({ tasks, loading, error, selectedId, onSelect, onRetry }: {
  tasks: EngineeringTaskSummary[]
  loading: boolean
  error: string | null
  selectedId: string | null
  onSelect: (id: string) => void
  onRetry: () => void
}) {
  return <section className="recent-tasks" aria-labelledby="recent-tasks-title" aria-busy={loading}>
    <div className="recent-heading"><div><p className="eyebrow">PERSISTED TASK HISTORY</p><h2 id="recent-tasks-title">Recent tasks</h2></div><span>Up to 50</span></div>
    {loading && <p className="history-state" role="status">Loading recent tasks…</p>}
    {!loading && error && <div className="history-state history-error" role="alert"><p>{error}</p><button type="button" className="text-button" onClick={onRetry}>Retry history</button></div>}
    {!loading && !error && tasks.length === 0 && <p className="history-state">No persisted tasks yet.</p>}
    {!loading && !error && tasks.length > 0 && <ul className="task-list">{tasks.map(task => <li key={task.id}>
      <button type="button" className={selectedId === task.id ? 'selected' : ''} aria-current={selectedId === task.id ? 'page' : undefined} onClick={() => onSelect(task.id)}>
        <span><strong>{task.status.replace(/([a-z])([A-Z])/g, '$1 $2')}</strong><time dateTime={task.updatedAt}>{new Date(task.updatedAt).toLocaleString()}</time></span>
        <code>{task.repository}</code><p>{task.originalRequirementPreview}</p>
      </button>
    </li>)}</ul>}
  </section>
}
