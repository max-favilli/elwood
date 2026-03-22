import { useState, useCallback, useRef } from 'react';
import Editor, { loader } from '@monaco-editor/react';
import { Toolbar } from './components/Toolbar';
import { PanelHeader, PanelButton } from './components/PanelHeader';
import { StatusBar } from './components/StatusBar';
import { ErrorPanel } from './components/ErrorPanel';
import { useElwood } from './hooks/useElwood';
import { GallerySidebar } from './components/GallerySidebar';
import { ShareModal } from './components/ShareModal';
import { compressToEncodedURIComponent, decompressFromEncodedURIComponent } from 'lz-string';
import { registerElwoodLanguage, ELWOOD_LANGUAGE_ID } from './editor/elwood-language';

// Register Elwood language when Monaco loads
loader.init().then(monaco => {
  registerElwoodLanguage(monaco);
});

const DEFAULT_EXPRESSION = '$.users[*] | where u => u.active | select u => u.name';

const DEFAULT_INPUT = JSON.stringify({
  users: [
    { name: 'Alice', age: 30, active: true },
    { name: 'Bob', age: 17, active: false },
    { name: 'Charlie', age: 25, active: true },
    { name: 'Diana', age: 42, active: true },
  ]
}, null, 2);

function App() {
  const [expression, setExpression] = useState(DEFAULT_EXPRESSION);
  const [input, setInput] = useState(DEFAULT_INPUT);
  const [exprHeight, setExprHeight] = useState(88);
  const { result, isRunning, run, debouncedRun } = useElwood();
  const [galleryOpen, setGalleryOpen] = useState(false);
  const [shareUrl, setShareUrl] = useState('');
  const resizing = useRef(false);
  const fileInputRef = useRef<HTMLInputElement>(null);

  // Run on expression or input change
  const handleExprChange = useCallback((value: string | undefined) => {
    const v = value ?? '';
    setExpression(v);
    debouncedRun(v, input);
  }, [input, debouncedRun]);

  const handleInputChange = useCallback((value: string | undefined) => {
    const v = value ?? '';
    setInput(v);
    debouncedRun(expression, v);
  }, [expression, debouncedRun]);

  const handleRun = useCallback(() => run(expression, input), [expression, input, run]);

  const handleExampleSelect = useCallback((script: string, inputJson: string) => {
    setExpression(script);
    setInput(inputJson);
    // Expand expression panel if script is multi-line
    const lineCount = script.split('\n').length;
    if (lineCount > 3) setExprHeight(Math.min(lineCount * 20 + 16, window.innerHeight * 0.4));
    setTimeout(() => run(script, inputJson), 50);
  }, [run]);

  // Keyboard shortcut: Ctrl+Enter / Cmd+Enter
  const handleKeyDown = useCallback((e: React.KeyboardEvent) => {
    if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') {
      e.preventDefault();
      handleRun();
    }
  }, [handleRun]);

  // Resize handle
  const handleMouseDown = useCallback(() => {
    resizing.current = true;
    const handleMouseMove = (e: MouseEvent) => {
      if (!resizing.current) return;
      const toolbarH = 40; // toolbar height
      const newH = Math.max(36, Math.min(window.innerHeight * 0.6, e.clientY - toolbarH));
      setExprHeight(newH);
    };
    const handleMouseUp = () => {
      resizing.current = false;
      document.removeEventListener('mousemove', handleMouseMove);
      document.removeEventListener('mouseup', handleMouseUp);
    };
    document.addEventListener('mousemove', handleMouseMove);
    document.addEventListener('mouseup', handleMouseUp);
  }, []);

  // File loading
  const handleLoadFile = useCallback(() => fileInputRef.current?.click(), []);
  const handleFileChange = useCallback((e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (!file) return;
    if (file.size > 5 * 1024 * 1024) {
      alert('File is larger than 5MB. Large files may slow down the browser.');
    }
    const reader = new FileReader();
    reader.onload = () => {
      const text = reader.result as string;
      setInput(text);
      debouncedRun(expression, text);
    };
    reader.readAsText(file);
    e.target.value = '';
  }, [expression, debouncedRun]);

  // Drop zone
  const [dragging, setDragging] = useState(false);
  const handleDrop = useCallback((e: React.DragEvent) => {
    e.preventDefault();
    setDragging(false);
    const file = e.dataTransfer.files[0];
    if (!file) return;
    const reader = new FileReader();
    reader.onload = () => {
      const text = reader.result as string;
      setInput(text);
      debouncedRun(expression, text);
    };
    reader.readAsText(file);
  }, [expression, debouncedRun]);

  const handleFormat = useCallback(() => {
    try {
      setInput(JSON.stringify(JSON.parse(input), null, 2));
    } catch { /* ignore */ }
  }, [input]);

  const handleClear = useCallback(() => setExpression(''), []);

  const handleCopy = useCallback(() => {
    navigator.clipboard.writeText(result.output);
  }, [result.output]);

  const handleShare = useCallback(() => {
    const payload = JSON.stringify({ e: expression, i: input });
    const compressed = compressToEncodedURIComponent(payload);
    const url = `${window.location.origin}${window.location.pathname}#data=${compressed}`;
    setShareUrl(url);
  }, [expression, input]);

  // Load from URL on mount
  useState(() => {
    const hash = window.location.hash;
    if (hash.startsWith('#data=')) {
      const data = decompressFromEncodedURIComponent(hash.slice(6));
      if (data) {
        try {
          const { e, i } = JSON.parse(data);
          if (e) setExpression(e);
          if (i) setInput(i);
          setTimeout(() => run(e, i), 100);
        } catch { /* ignore */ }
      }
    }
  });

  const monacoOptions = {
    fontSize: 13,
    lineNumbers: 'off' as const,
    minimap: { enabled: false },
    scrollBeyondLastLine: false,
    wordWrap: 'on' as const,
    padding: { top: 8 },
    renderLineHighlight: 'none' as const,
    overviewRulerLanes: 0,
    hideCursorInOverviewRuler: true,
    overviewRulerBorder: false,
    scrollbar: { vertical: 'auto' as const, horizontal: 'auto' as const },
  };

  return (
    <div className="h-full flex flex-col" onKeyDown={handleKeyDown}>
      <Toolbar
        onRun={handleRun}
        onExamples={() => setGalleryOpen(true)}
        onShare={handleShare}
        isRunning={isRunning}
      />

      {/* Expression panel */}
      <div className="shrink-0 border-b border-border" style={{ height: exprHeight }}>
        <PanelHeader
          title="Expression"
          right={<PanelButton onClick={handleClear}>Clear</PanelButton>}
        />
        <div className="h-[calc(100%-32px)]">
          <Editor
            defaultLanguage={ELWOOD_LANGUAGE_ID}
            theme="vs-dark"
            value={expression}
            onChange={handleExprChange}
            options={{ ...monacoOptions, lineNumbers: 'off' }}
          />
        </div>
      </div>

      {/* Resize handle */}
      <div
        onMouseDown={handleMouseDown}
        className="h-1 bg-border hover:bg-accent cursor-row-resize shrink-0 transition-colors"
      />

      {/* Data panels (input + output side by side) */}
      <div className="flex-1 flex min-h-0">
        {/* Input panel */}
        <div
          className={`w-1/2 flex flex-col border-r border-border ${dragging ? 'ring-2 ring-accent ring-inset' : ''}`}
          onDragOver={(e) => { e.preventDefault(); setDragging(true); }}
          onDragLeave={() => setDragging(false)}
          onDrop={handleDrop}
        >
          <PanelHeader
            title="Input JSON"
            right={
              <>
                <PanelButton onClick={handleLoadFile}>Load File</PanelButton>
                <PanelButton onClick={handleFormat}>Format</PanelButton>
              </>
            }
          />
          <div className="flex-1 min-h-0">
            <Editor
              defaultLanguage="json"
              theme="vs-dark"
              value={input}
              onChange={handleInputChange}
              options={monacoOptions}
            />
          </div>
          <input ref={fileInputRef} type="file" accept=".json" className="hidden" onChange={handleFileChange} />
        </div>

        {/* Output panel */}
        <div className="w-1/2 flex flex-col">
          <PanelHeader
            title="Output"
            right={
              <>
                {result.timeMs > 0 && (
                  <span className="text-[10px] text-success mr-1">✓ {result.timeMs.toFixed(1)}ms</span>
                )}
                <PanelButton onClick={handleCopy}>Copy</PanelButton>
              </>
            }
          />
          <div className="flex-1 min-h-0">
            <Editor
              defaultLanguage="json"
              theme="vs-dark"
              value={result.output}
              options={{ ...monacoOptions, readOnly: true }}
            />
          </div>
          {result.error && <ErrorPanel error={result.error} />}
        </div>
      </div>

      <StatusBar
        success={result.success}
        inputSize={new TextEncoder().encode(input).length}
        outputSize={new TextEncoder().encode(result.output).length}
        timeMs={result.timeMs}
        error={result.error}
      />

      <GallerySidebar
        open={galleryOpen}
        onClose={() => setGalleryOpen(false)}
        onSelect={handleExampleSelect}
      />

      <ShareModal
        open={shareUrl !== ''}
        url={shareUrl}
        onClose={() => setShareUrl('')}
      />
    </div>
  );
}

export default App;
