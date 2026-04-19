interface LargeFileConfirmModalProps {
  open: boolean;
  sizeMB: number;
  onConfirm: () => void;
  onCancel: () => void;
}

export function LargeFileConfirmModal({ open, sizeMB, onConfirm, onCancel }: LargeFileConfirmModalProps) {
  if (!open) return null;

  return (
    <>
      <div className="fixed inset-0 bg-black/50 z-50" onClick={onCancel} />
      <div className="fixed top-1/3 left-1/2 -translate-x-1/2 -translate-y-1/2 bg-bg-secondary border border-border rounded-lg shadow-xl z-50 w-[440px] max-w-[90vw]">
        <div className="flex items-center justify-between px-4 py-3 border-b border-border">
          <span className="text-sm font-medium text-text-primary">Disable Large File Mode?</span>
          <button onClick={onCancel} className="text-text-secondary hover:text-text-primary text-lg">&times;</button>
        </div>
        <div className="p-4 space-y-3">
          <p className="text-xs text-text-secondary">
            Your input is <span className="font-semibold text-text-primary">{sizeMB.toFixed(1)} MB</span>.
            Disabling Large File Mode will enable full syntax highlighting and language
            features, which may cause the editor to become <span className="font-semibold text-text-primary">slow or unresponsive</span>.
          </p>
          <p className="text-[10px] text-text-secondary/60">
            You can re-enable Large File Mode at any time from the input panel header.
          </p>
          <div className="flex justify-end gap-2 pt-1">
            <button
              onClick={onCancel}
              className="px-3 py-1.5 text-xs text-text-secondary hover:text-text-primary bg-bg-tertiary border border-border rounded hover:bg-bg-hover transition-colors"
            >
              Keep Large File Mode
            </button>
            <button
              onClick={onConfirm}
              className="px-3 py-1.5 text-xs text-white bg-amber-600 hover:bg-amber-500 rounded transition-colors"
            >
              Disable Anyway
            </button>
          </div>
        </div>
      </div>
    </>
  );
}
