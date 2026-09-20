import { StrictMode } from "react";
import { createRoot } from "react-dom/client";

import { App } from "./App";
import type { ReplayFile } from "./adapter/replayFile";
import "./index.css";

/**
 * The replay is embedded in the page as a JSON island, not fetched.
 *
 * A `fetch()` would fail the moment the page is opened from `file://`, which is
 * exactly how MTGA+ opens it — no server, no port, no firewall prompt. A
 * `<script type="application/json">` is inert (the browser does not execute it)
 * and readable with `textContent`.
 */
function readReplay(): ReplayFile | null {
  const node = document.getElementById("replay-data");
  if (!node) return null;
  try {
    const parsed = JSON.parse(node.textContent ?? "") as ReplayFile;
    if (!parsed || !Array.isArray(parsed.steps)) return null;
    return parsed;
  } catch {
    return null;
  }
}

const replay = readReplay();
const root = createRoot(document.getElementById("root")!);

root.render(
  <StrictMode>
    {replay ? (
      <App replay={replay} />
    ) : (
      <div className="flex h-screen items-center justify-center bg-[#12121e] p-8 text-center text-slate-400">
        No replay data in this page. MTGA+ writes it in when it exports a match;
        the template on its own has nothing to show.
      </div>
    )}
  </StrictMode>,
);
