import { ReactNode } from 'react';

interface PanelHeaderProps {
  title: string;
  right?: ReactNode;
}

export function PanelHeader({ title, right }: PanelHeaderProps) {
  return (
    <div className="flex items-center justify-between h-8 px-3 bg-bg-tertiary border-b border-border shrink-0">
      <span className="text-xs text-text-secondary uppercase tracking-wider">{title}</span>
      {right && <div className="flex items-center gap-1">{right}</div>}
    </div>
  );
}

export function PanelButton({ onClick, children }: { onClick: () => void; children: ReactNode }) {
  return (
    <button
      onClick={onClick}
      className="text-[10px] text-text-secondary hover:text-text-primary px-1.5 py-0.5 hover:bg-bg-hover rounded transition-colors"
    >
      {children}
    </button>
  );
}
