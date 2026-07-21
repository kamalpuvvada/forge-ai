interface Props {
  diff: string
  filePath: string
}

type DiffLineKind = 'added' | 'deleted' | 'hunk' | 'metadata' | 'context'

export function UnifiedDiff({ diff, filePath }: Props) {
  return <pre className="unified-diff" tabIndex={0} aria-label={`Unified diff for ${filePath}`}>
    <code>{splitPreservingLineEndings(diff).map((line, index) => {
      const content = line.replace(/(?:\r\n|\r|\n)$/, '')
      const kind = classifyDiffLine(content)
      const semanticLabel = kind === 'added' ? 'Added line' : kind === 'deleted' ? 'Deleted line' : undefined
      return <span key={index} className={`diff-line diff-line-${kind}`}
        aria-label={semanticLabel ? `${semanticLabel}: ${content}` : undefined}>{line}</span>
    })}</code>
  </pre>
}

function classifyDiffLine(line: string): DiffLineKind {
  if (line.startsWith('diff --git') || line.startsWith('index ') || line.startsWith('---') || line.startsWith('+++'))
    return 'metadata'
  if (line.startsWith('@@')) return 'hunk'
  if (line.startsWith('+')) return 'added'
  if (line.startsWith('-')) return 'deleted'
  return 'context'
}

function splitPreservingLineEndings(value: string) {
  if (value.length === 0) return ['']
  return value.match(/[^\r\n]*(?:\r\n|\r|\n|$)/g)!.filter((line, index, lines) =>
    line.length > 0 || index < lines.length - 1)
}
