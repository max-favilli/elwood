interface StatusBarProps {
  success: boolean;
  inputSize: number;
  outputSize: number;
  timeMs: number;
  error: string | null;
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

export function StatusBar({ success, inputSize, outputSize, timeMs, error }: StatusBarProps) {
  return (
    <div className="flex items-center justify-between h-6 px-3 bg-accent text-white text-[11px] shrink-0">
      <div className="flex items-center gap-3">
        <span>
          {error ? '✕ Error' : success ? '✓ Ready' : '…'}
        </span>
        <span>Input: {formatBytes(inputSize)}</span>
        {outputSize > 0 && <span>Output: {formatBytes(outputSize)}</span>}
        {timeMs > 0 && <span>{timeMs.toFixed(1)}ms</span>}
      </div>
      <span className="text-white/60">v0.1.0</span>
    </div>
  );
}
