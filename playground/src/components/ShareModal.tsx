import { useState, useRef, useEffect } from 'react';

interface ShareModalProps {
  open: boolean;
  url: string;
  loading?: boolean;
  error?: string | null;
  onClose: () => void;
}

export function ShareModal({ open, url, loading, error, onClose }: ShareModalProps) {
  const [copied, setCopied] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    if (open) {
      setCopied(false);
      if (url) setTimeout(() => inputRef.current?.select(), 50);
    }
  }, [open, url]);

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
          <button onClick={onClose} className="text-text-secondary hover:text-text-primary text-lg">&times;</button>
        </div>
        <div className="p-4">
          {loading ? (
            <div className="flex items-center gap-3 py-2">
              <div className="w-4 h-4 border-2 border-accent border-t-transparent rounded-full animate-spin" />
              <span className="text-xs text-text-secondary">Uploading data…</span>
            </div>
          ) : error ? (
            <div className="text-xs text-red-400 py-2">
              <p className="font-medium mb-1">Failed to create share link</p>
              <p className="text-text-secondary">{error}</p>
            </div>
          ) : (
            <>
              <p className="text-xs text-text-secondary mb-3">
                Anyone with this link will see the same expression and input data.
                {url.includes('#s=') && (
                  <span className="block mt-1 text-text-secondary/60">
                    Link expires after 90 days.
                  </span>
                )}
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
            </>
          )}
        </div>
      </div>
    </>
  );
}
