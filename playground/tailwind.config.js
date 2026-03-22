/** @type {import('tailwindcss').Config} */
export default {
  content: ['./index.html', './src/**/*.{js,ts,jsx,tsx}'],
  theme: {
    extend: {
      colors: {
        'bg-primary': '#1e1e1e',
        'bg-secondary': '#252526',
        'bg-tertiary': '#2d2d30',
        'bg-hover': '#3e3e42',
        'bg-active': '#094771',
        'border': '#3e3e42',
        'text-primary': '#cccccc',
        'text-secondary': '#969696',
        'text-muted': '#5a5a5a',
        'accent': '#007acc',
        'error': '#f48747',
        'error-bg': '#3a1d1d',
        'success': '#89d185',
      },
      fontFamily: {
        mono: ['JetBrains Mono', 'Consolas', 'Courier New', 'monospace'],
      },
    },
  },
  plugins: [],
};
