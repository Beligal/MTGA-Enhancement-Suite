import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import { viteSingleFile } from "vite-plugin-singlefile";
import { renameSync, rmSync } from "node:fs";
import { resolve } from "node:path";

/**
 * Builds the replay viewer into ONE html file.
 *
 * Everything inline, nothing fetched: the page is opened from `file://` by
 * `Application.OpenURL`, where a second request for a `.js` beside it is a
 * separate file that can go missing, and where `fetch` has no origin worth
 * having. One file is also what makes a replay something you can keep or send
 * to somebody.
 *
 * The output lands straight in `Plugin/Resources/`, where the .csproj embeds it
 * in the assembly. The built file IS committed: `dotnet build` must not need
 * Node.
 */
const OUT_DIR = resolve(import.meta.dirname, "../Plugin/Resources");
const OUT_FILE = "replay-viewer.html";

/**
 * Vite names the bundle after its entry, `index.html`. The embedded resource is
 * `replay-viewer.html`, because "index.html" inside a DLL says nothing about
 * what it is. Renaming after the write is the whole plugin.
 */
function renameOutput() {
  return {
    name: "mtgaplus-rename-output",
    closeBundle() {
      renameSync(resolve(OUT_DIR, "index.html"), resolve(OUT_DIR, OUT_FILE));
      // vite-plugin-singlefile inlines the js and css but still emits the
      // originals; with emptyOutDir off they would pile up build after build.
      rmSync(resolve(OUT_DIR, "assets"), { recursive: true, force: true });
    },
  };
}

export default defineConfig({
  plugins: [react(), tailwindcss(), viteSingleFile(), renameOutput()],
  build: {
    outDir: "../Plugin/Resources",
    emptyOutDir: false,
    assetsInlineLimit: 100000000,
    cssCodeSplit: false,
    rollupOptions: {
      output: { inlineDynamicImports: true },
    },
  },
});
