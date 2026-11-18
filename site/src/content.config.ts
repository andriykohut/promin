import { defineCollection } from 'astro:content';
import { glob } from 'astro/loaders';
import { z } from 'astro/zod';

// One folder per language; the same file name in each is the same page.
const docs = defineCollection({
  loader: glob({ pattern: '**/*.md', base: './src/content/docs', generateId: ({ entry }) => entry.replace(/\.md$/, '') }),
  schema: z.object({ title: z.string(), order: z.number() }),
});

export const collections = { docs };
