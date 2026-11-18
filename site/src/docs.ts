import { getCollection, type CollectionEntry } from 'astro:content';
import type { Lang } from './strings';

/** Returns a doc's path below the language prefix: `docs/` for the language's `index.md`, else `docs/<name>/`. */
export const docPath = (doc: CollectionEntry<'docs'>) => {
  const name = doc.id.split('/')[1];
  return name === 'index' ? 'docs/' : `docs/${name}/`;
};

/** Returns one language's docs in sidebar order. */
export const docsIn = async (lang: Lang) =>
  (await getCollection('docs', (doc) => doc.id.startsWith(`${lang}/`))).sort((a, b) => a.data.order - b.data.order);

/** Returns the static paths for one language's `docs/[...slug]` route. */
export const docRoutes = async (lang: Lang) =>
  (await docsIn(lang)).map((doc) => ({
    params: { slug: docPath(doc).slice('docs/'.length, -1) || undefined },
    props: { doc },
  }));
