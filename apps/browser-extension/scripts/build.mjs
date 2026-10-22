import { build } from "esbuild";
import { cp, mkdir, rm } from "node:fs/promises";

const outdir = new URL("../dist/", import.meta.url);
await rm(outdir, { recursive: true, force: true });
await mkdir(outdir, { recursive: true });

await build({
  entryPoints: {
    background: new URL("../src/background.ts", import.meta.url).pathname.slice(1),
    content: new URL("../src/content.ts", import.meta.url).pathname.slice(1),
    sidepanel: new URL("../src/sidepanel.ts", import.meta.url).pathname.slice(1)
  },
  bundle: true,
  outdir: outdir.pathname.slice(1),
  format: "iife",
  target: "chrome116",
  sourcemap: false,
  minify: false
});

await cp(new URL("../public/", import.meta.url), outdir, { recursive: true });
