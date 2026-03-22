interface ErrorPanelProps {
  error: string;
}

export function ErrorPanel({ error }: ErrorPanelProps) {
  return (
    <div className="bg-error-bg border-t border-error/30 p-3 shrink-0">
      <pre className="text-error text-xs whitespace-pre-wrap font-mono leading-relaxed">{error}</pre>
    </div>
  );
}
