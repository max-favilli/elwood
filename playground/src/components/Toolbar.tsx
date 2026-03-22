
interface ToolbarProps {
  onRun: () => void;
  onExamples: () => void;
  onShare: () => void;
  isRunning: boolean;
}

export function Toolbar({ onRun, onExamples, onShare, isRunning }: ToolbarProps) {

  return (
    <div className="flex items-center justify-between h-10 px-3 bg-bg-secondary border-b border-border shrink-0">
      {/* Left */}
      <div className="flex items-center gap-2">
        <span className="font-mono font-semibold text-text-primary text-sm tracking-wide mr-1">
          {'{elwood}'}
        </span>
        <div className="w-px h-5 bg-border" />
        <button
          onClick={onExamples}
          className="text-xs text-text-secondary hover:text-text-primary px-2 py-1 hover:bg-bg-hover rounded transition-colors"
        >
          Examples
        </button>
        <div className="w-px h-5 bg-border" />
        <button
          onClick={onRun}
          disabled={isRunning}
          className="text-xs font-medium text-white bg-accent hover:bg-accent/80 px-3 py-1 rounded flex items-center gap-1.5 transition-colors disabled:opacity-50"
        >
          <span>▶</span> Run
        </button>
        <button
          onClick={onShare}
          className="flex items-center gap-1.5 text-xs text-text-secondary hover:text-text-primary px-2 py-1 hover:bg-bg-hover rounded transition-colors"
        >
          <svg width="13" height="13" viewBox="0 0 16 16" fill="currentColor">
            <path d="M13.5 1a1.5 1.5 0 100 3 1.5 1.5 0 000-3zM11 2.5a2.5 2.5 0 11.603 1.628l-6.718 3.12a2.499 2.499 0 010 1.504l6.718 3.12a2.5 2.5 0 11-.488.876l-6.718-3.12a2.5 2.5 0 110-3.256l6.718-3.12A2.5 2.5 0 0111 2.5zm-8.5 4a1.5 1.5 0 100 3 1.5 1.5 0 000-3zm11 5.5a1.5 1.5 0 100 3 1.5 1.5 0 000-3z"/>
          </svg>
          Share
        </button>
      </div>

      {/* Right: Docs + GitHub */}
      <div className="flex items-center gap-2">
        <a
          href="https://github.com/max-favilli/elwood/blob/main/docs/syntax-reference.md"
          target="_blank"
          rel="noopener"
          className="flex items-center gap-1.5 text-xs text-text-secondary hover:text-text-primary px-2 py-1 hover:bg-bg-hover rounded transition-colors"
        >
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/>
            <polyline points="14 2 14 8 20 8"/>
            <line x1="16" y1="13" x2="8" y2="13"/>
            <line x1="16" y1="17" x2="8" y2="17"/>
          </svg>
          Docs
        </a>
        <a
          href="https://github.com/max-favilli/elwood"
          target="_blank"
          rel="noopener"
          className="flex items-center gap-1.5 text-xs text-text-secondary hover:text-text-primary px-2 py-1 hover:bg-bg-hover rounded transition-colors"
        >
          <svg width="16" height="16" viewBox="0 0 16 16" fill="currentColor">
            <path d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.013 8.013 0 0016 8c0-4.42-3.58-8-8-8z"/>
          </svg>
          GitHub
        </a>
      </div>
    </div>
  );
}
