import Markdown from 'react-markdown';

interface ExplanationModalProps {
  open: boolean;
  title: string;
  explanation: string;
  isBenchmark: boolean;
  onClose: () => void;
  onLoad: () => void;
}

export function ExplanationModal({ open, title, explanation, isBenchmark, onClose, onLoad }: ExplanationModalProps) {
  if (!open) return null;

  return (
    <>
      <div className="fixed inset-0 bg-black/60 z-[60]" onClick={onClose} />
      <div className="fixed inset-8 bg-bg-secondary border border-border rounded-lg shadow-2xl z-[60] flex flex-col">
        {/* Header */}
        <div className="flex items-center justify-between px-5 py-3 border-b border-border shrink-0">
          <span className="text-sm font-medium text-text-primary">{title}</span>
          <div className="flex items-center gap-2">
            {!isBenchmark && (
              <button
                onClick={() => { onLoad(); onClose(); }}
                className="text-xs font-medium text-white bg-accent hover:bg-accent/80 px-3 py-1 rounded transition-colors"
              >
                Load Example
              </button>
            )}
            <button onClick={onClose} className="text-text-secondary hover:text-text-primary text-xl leading-none px-2">×</button>
          </div>
        </div>

        {/* Content */}
        <div className="flex-1 overflow-y-auto px-6 py-4">
          <div className="max-w-3xl mx-auto
            text-sm text-text-secondary leading-relaxed
            [&_h1]:text-text-primary [&_h1]:text-lg [&_h1]:font-semibold [&_h1]:mt-4 [&_h1]:mb-2
            [&_h2]:text-text-primary [&_h2]:text-base [&_h2]:font-semibold [&_h2]:mt-5 [&_h2]:mb-2
            [&_h3]:text-text-primary [&_h3]:text-sm [&_h3]:font-medium [&_h3]:mt-4 [&_h3]:mb-1
            [&_p]:my-2
            [&_code]:bg-bg-tertiary [&_code]:px-1.5 [&_code]:py-0.5 [&_code]:rounded [&_code]:text-xs [&_code]:font-mono [&_code]:text-accent
            [&_pre]:bg-bg-primary [&_pre]:p-4 [&_pre]:rounded-lg [&_pre]:my-3 [&_pre]:overflow-x-auto [&_pre]:border [&_pre]:border-border
            [&_pre_code]:bg-transparent [&_pre_code]:p-0 [&_pre_code]:text-text-primary [&_pre_code]:text-xs
            [&_table]:w-full [&_table]:text-xs [&_table]:my-3
            [&_th]:text-left [&_th]:text-text-muted [&_th]:border-b [&_th]:border-border [&_th]:pb-1.5 [&_th]:pr-3 [&_th]:font-medium
            [&_td]:border-b [&_td]:border-border/30 [&_td]:py-1 [&_td]:pr-3
            [&_ul]:list-disc [&_ul]:pl-5 [&_ul]:my-2
            [&_ol]:list-decimal [&_ol]:pl-5 [&_ol]:my-2
            [&_li]:my-1
            [&_strong]:text-text-primary [&_strong]:font-medium
            [&_blockquote]:border-l-2 [&_blockquote]:border-accent [&_blockquote]:pl-3 [&_blockquote]:my-2 [&_blockquote]:text-text-muted [&_blockquote]:italic
            [&_hr]:border-border [&_hr]:my-4
          ">
            <Markdown>{explanation}</Markdown>
          </div>
        </div>
      </div>
    </>
  );
}
