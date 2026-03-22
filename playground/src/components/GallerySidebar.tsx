import { useState, useMemo } from 'react';
import galleryData from '../data/gallery.json';
import { ExplanationModal } from './ExplanationModal';

interface Example {
  id: string;
  title: string;
  category: string;
  script: string;
  input: string | null;
  description: string;
  explanation: string;
  isBenchmark: boolean;
}

interface GallerySidebarProps {
  open: boolean;
  onClose: () => void;
  onSelect: (script: string, input: string) => void;
}

function getScriptPreview(script: string): string {
  const lines = script.split('\n');
  for (const line of lines) {
    const trimmed = line.trim();
    if (trimmed && trimmed !== '{' && trimmed !== '}' && trimmed !== '[' && trimmed !== ']') {
      return trimmed.slice(0, 70);
    }
  }
  return script.trim().slice(0, 70);
}

export function GallerySidebar({ open, onClose, onSelect }: GallerySidebarProps) {
  const [search, setSearch] = useState('');
  const [infoExample, setInfoExample] = useState<Example | null>(null);
  const examples = galleryData as Example[];

  const grouped = useMemo(() => {
    const filtered = search
      ? examples.filter(e =>
          e.title.toLowerCase().includes(search.toLowerCase()) ||
          e.category.toLowerCase().includes(search.toLowerCase()) ||
          e.id.toLowerCase().includes(search.toLowerCase()))
      : examples;

    const groups = new Map<string, Example[]>();
    for (const ex of filtered) {
      if (!groups.has(ex.category)) groups.set(ex.category, []);
      groups.get(ex.category)!.push(ex);
    }
    return groups;
  }, [search, examples]);

  const handleSelect = (ex: Example) => {
    if (ex.isBenchmark) return;
    onSelect(ex.script, ex.input ?? '{}');
    onClose();
  };

  const handleInfoLoad = () => {
    if (infoExample && !infoExample.isBenchmark) {
      onSelect(infoExample.script, infoExample.input ?? '{}');
      onClose();
    }
  };

  return (
    <>
      {open && <div className="fixed inset-0 bg-black/50 z-40" onClick={onClose} />}

      <div
        className={`fixed top-0 left-0 h-full w-[420px] bg-bg-secondary border-r border-border z-50 flex flex-col transform transition-transform duration-200 ${
          open ? 'translate-x-0' : '-translate-x-full'
        }`}
      >
        <div className="flex items-center justify-between h-10 px-3 border-b border-border shrink-0">
          <span className="text-sm font-medium text-text-primary">Examples</span>
          <button onClick={onClose} className="text-text-secondary hover:text-text-primary text-lg leading-none px-1">×</button>
        </div>

        <div className="p-2 border-b border-border shrink-0">
          <input
            type="text"
            value={search}
            onChange={e => setSearch(e.target.value)}
            placeholder="Search examples..."
            className="w-full bg-bg-primary border border-border rounded px-2 py-1 text-xs text-text-primary placeholder:text-text-muted focus:outline-none focus:border-accent"
            autoFocus={open}
          />
        </div>

        <div className="flex-1 overflow-y-auto">
          {[...grouped.entries()].map(([category, items]) => (
            <div key={category}>
              <div className="px-3 py-1.5 text-[10px] uppercase tracking-wider text-text-muted bg-bg-tertiary sticky top-0">
                {category}
              </div>
              {items.map(ex => (
                <div
                  key={ex.id}
                  onClick={() => handleSelect(ex)}
                  className={`px-3 py-2 border-b border-border/50 transition-colors ${
                    ex.isBenchmark ? 'opacity-50 cursor-default' : 'hover:bg-bg-hover cursor-pointer'
                  }`}
                >
                  <div className="flex items-center justify-between">
                    <div className="flex items-center gap-2">
                      <span className="text-xs text-text-primary">{ex.title}</span>
                      {ex.isBenchmark && (
                        <span className="text-[9px] bg-bg-tertiary text-text-muted px-1 rounded">CLI</span>
                      )}
                    </div>
                    {ex.explanation && (
                      <button
                        onClick={(e) => { e.stopPropagation(); setInfoExample(ex); }}
                        className="text-[10px] text-accent hover:text-accent/80 px-1.5 py-0.5 hover:bg-bg-hover rounded transition-colors"
                        title="View explanation"
                      >
                        ℹ Info
                      </button>
                    )}
                  </div>
                  <div className="text-[10px] text-text-muted mt-0.5 font-mono truncate">
                    {getScriptPreview(ex.script)}
                  </div>
                </div>
              ))}
            </div>
          ))}

          {grouped.size === 0 && (
            <div className="p-4 text-xs text-text-muted text-center">No matching examples</div>
          )}
        </div>

        <div className="px-3 py-2 border-t border-border text-[10px] text-text-muted shrink-0">
          {examples.length} examples from spec/test-cases/
        </div>
      </div>

      <ExplanationModal
        open={infoExample !== null}
        title={infoExample?.title ?? ''}
        explanation={infoExample?.explanation ?? ''}
        isBenchmark={infoExample?.isBenchmark ?? false}
        onClose={() => setInfoExample(null)}
        onLoad={handleInfoLoad}
      />
    </>
  );
}
