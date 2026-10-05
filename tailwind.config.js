/** Same design tokens as the React storefront (../tailwind.config.js); content globs point at Razor/C# sources. */
/** @type {import('tailwindcss').Config} */
module.exports = {
  content: [
    './src/Nuraherbex.UI/**/*.{razor,cs,html}',
    './src/Nuraherbex.Web/wwwroot/index.html',
    './src/Nuraherbex.Maui/wwwroot/index.html',
  ],
  theme: {
    extend: {
      colors: {
        background: '#07050b',
        obsidian: '#090610',
        header: '#fff3bf',
        brand: {
          purple: '#9929ea',
          lavender: '#cc66da',
          gold: '#fff3bf',
          cream: '#fff3bf',
          accent: '#cc66da',
        },
      },
      fontFamily: {
        sans: ['"EB Garamond"', 'Garamond', 'Georgia', 'serif'],
        serif: ['"EB Garamond"', 'Garamond', 'Georgia', 'serif'],
        display: ['"Momo Trust Display"', '"Plus Jakarta Sans"', 'system-ui', 'sans-serif'],
        heading: ['"Momo Trust Display"', '"Plus Jakarta Sans"', 'system-ui', 'sans-serif'],
        mono: ['JetBrains Mono', 'monospace'],
      },
      keyframes: {
        breathe: {
          '0%, 100%': { opacity: '0.4', transform: 'translate(-50%, -50%) scale(0.96)' },
          '50%': { opacity: '0.75', transform: 'translate(-50%, -50%) scale(1.04)' },
        },
        heartbeat: {
          '0%, 100%': { transform: 'scale(1)' },
          '14%': { transform: 'scale(1.28)' },
          '28%': { transform: 'scale(1)' },
          '42%': { transform: 'scale(1.22)' },
          '70%': { transform: 'scale(1)' },
        },
        ticker: {
          '0%': { transform: 'translateX(0)' },
          '100%': { transform: 'translateX(-50%)' },
        },
        'footer-marquee': {
          '0%': { transform: 'translateX(0)' },
          '100%': { transform: 'translateX(-50%)' },
        },
        meniscus: {
          '0%, 100%': { transform: 'scaleY(1)' },
          '50%': { transform: 'scaleY(1.4)' },
        },
        equalizer: {
          '0%, 100%': { height: '8px' },
          '50%': { height: '36px' },
        },
      },
      animation: {
        breathe: 'breathe 7s ease-in-out infinite',
        heartbeat: 'heartbeat 1.6s ease-in-out infinite',
        ticker: 'ticker 22s linear infinite',
        'footer-marquee': 'footer-marquee 24s linear infinite',
        meniscus: 'meniscus 3.5s ease-in-out infinite',
        equalizer: 'equalizer 0.8s ease-in-out infinite alternate',
      },
    },
  },
  plugins: [],
};
