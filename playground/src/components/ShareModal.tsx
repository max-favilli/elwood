import { useState, useRef, useEffect } from 'react';

interface ShareModalProps {
  open: boolean;
  url: string;
  onClose: () => void;
}

export function ShareModal({ open, url, onClose }: ShareModalProps) {
  const [copied, setCopied] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    if (open) {
      setCopied(false);
      setTimeout(() => inputRef.current?.select(), 50);
    }
  }, [open]);

  if (!open) return null;

  const handleCopy = async () => {
    try {
      await navigator.clipboard.writeText(url);
      setCopied(true);
    } catch {
      inputRef.current?.select();
      document.execCommand('copy');
      setCopied(true);
    }
  };

  return (
    <>
      <div className="fixed inset-0 bg-black/50 z-50" onClick={onClose} />
      <div className="fixed top-1/3 left-1/2 -translate-x-1/2 -translate-y-1/2 bg-bg-secondary border border-border rounded-lg shadow-xl z-50 w-[500px] max-w-[90vw]">
        <div className="flex items-center justify-between px-4 py-3 border-b border-border">
          <span className="text-sm font-medium text-text-primary">Share Playground</span>
          <button onClick={onClose} className="text-text-secondary hover:text-text-primary text-lg">×</button>
        </div>
        <div className="p-4">
          <p className="text-xs text-text-secondary mb-3">
            Anyone with this link will see the same expression and input data.
          </p>
          <div className="flex gap-2">
            <input
              ref={inputRef}
              type="text"
              readOnly
              value={url}
              className="flex-1 bg-bg-primary border border-border rounded px-2 py-1.5 text-xs text-text-primary font-mono focus:outline-none focus:border-accent"
            />
            <button
              onClick={handleCopy}
              className="bg-accent hover:bg-accent/80 text-white text-xs px-4 py-1.5 rounded transition-colors"
            >
              {copied ? '✓ Copied' : 'Copy'}
            </button>
          </div>
        </div>
      </div>
    </>
  );
}
