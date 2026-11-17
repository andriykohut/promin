import { defineConfig, fontProviders } from 'astro/config';

export default defineConfig({
  site: 'https://andriykohut.github.io',
  base: '/promin',
  trailingSlash: 'always',
  fonts: [
    {
      name: 'Unbounded',
      cssVariable: '--font-display',
      provider: fontProviders.google(),
      weights: [500, 700],
      styles: ['normal'],
      subsets: ['latin', 'cyrillic', 'cyrillic-ext'],
      fallbacks: ['sans-serif'],
    },
    {
      name: 'IBM Plex Sans',
      cssVariable: '--font-body',
      provider: fontProviders.google(),
      weights: [400, 600],
      styles: ['normal'],
      subsets: ['latin', 'cyrillic', 'cyrillic-ext'],
      fallbacks: ['sans-serif'],
    },
  ],
});
