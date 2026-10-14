using System;
using System.Collections;
using System.IO;
using System.Linq;
using MTGAEnhancementSuite.Features;
using MTGAEnhancementSuite.Features.CollectionExport;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static MTGAEnhancementSuite.UI.ListModal;

namespace MTGAEnhancementSuite.UI
{
    /// <summary>
    /// La finestra dell'export della collezione: scelta del formato, stato
    /// dell'indice Scryfall, e dopo il salvataggio il riepilogo di cosa e'
    /// entrato nel file e cosa no.
    ///
    /// Si apre dal pulsante "Export…" del pannello Impostazioni, che prima
    /// apriva direttamente la finestra di salvataggio: il formato era uno
    /// solo. La riga del pannello non e' cambiata (le sue ancore sono a mano).
    ///
    /// **La rete non blocca mai l'interfaccia.** L'indice Scryfall si scarica
    /// solo se l'utente lo chiede, su un thread suo
    /// (<see cref="ScryfallIndexDownloader"/>); qui una coroutine legge lo
    /// stato due volte al secondo. Senza indice l'export funziona lo stesso,
    /// per nome, e la finestra lo dice.
    /// </summary>
    internal static class CollectionExportWindow
    {
        private static ModalFrame _frame;

        // Per la sessione: niente in settings.json, per non aggiungere
        // un'opzione che nessuno ha chiesto. Si riparte da Moxfield.
        private static ExportProfile _profile = ExportProfile.All[0];

        private static ScryfallArenaIndex _index;
        private static bool _indexRead;
        private static string _indexError;
        private static ScryfallIndexDownloader _downloader;
        private static TextMeshProUGUI _indexStatus;
        private static CollectionExporter.Result _last;

        /// <summary>Accanto alle statistiche, condiviso fra installazione di sviluppo e stabile.</summary>
        private static string IndexPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MTGAEnhancementSuite", "scryfall", "arena-index.tsv");

        public static void Show()
        {
            _last = null;
            if (!_indexRead)
            {
                _indexRead = true;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                _index = ScryfallIndexStore.TryLoad(IndexPath, out _indexError);
                Plugin.Log.LogInfo(_index != null
                    ? $"CollectionExport: Scryfall index {_index.Count} prints from {_index.BuiltUtc:u}, read in {sw.ElapsedMilliseconds} ms"
                    : $"CollectionExport: no Scryfall index ({_indexError ?? "not downloaded"})");
            }
            Redraw();
        }

        public static void Close()
        {
            _frame?.Destroy();
            _frame = null;
            _indexStatus = null;
            // Il download, se c'e', continua: chiudere la finestra non e'
            // chiedere di fermarlo, e alla riapertura se ne vede lo stato.
        }

        // ------------------------------------------------------------------

        private static void Redraw()
        {
            _frame?.Destroy();
            _frame = ListModal.Open("MTGAES_CollectionExport", "Export Collection", richTitle: false, onClose: Close,
                anchorMin: new Vector2(0.27f, 0.10f), anchorMax: new Vector2(0.73f, 0.90f));
            var list = _frame.List;

            Heading(list, "Format");
            foreach (var p in ExportProfile.All) FormatRow(list, p);

            Spacer(list);
            Heading(list, "Card identification");
            // Chiesto dall'utente il 2026-10-14: senza questa riga non si
            // capiva a cosa servisse scaricare qualcosa da Scryfall.
            Text(list, "Many cards exist in several printings (different sets, art or numbers). Arena's card ids " +
                       "mean nothing to collection sites, so this list from Scryfall translates them into the exact " +
                       "set and collector number. Without it, cards are exported by name only and the site picks a " +
                       "printing; ManaBox and MTGGoldfish need it.",
                 TextSecondary, 13, 2.4f);
            IndexRow(list);

            Spacer(list);
            Text(list, "Your Arena collection is digital. Some sites treat an imported file as physical cards " +
                       "(prices, trade lists). On Moxfield, set the collection location to \"Arena\" when importing.",
                 TextSecondary, 13, 2.0f);

            if (_last != null) Summary(list, _last);

            Button(_frame.Footer, "Save " + _profile.Extension.ToUpperInvariant() + "…",
                new Vector2(0f, 0f), new Vector2(0.40f, 1f), AccentBlue, Save);
            Button(_frame.Footer, "Close", new Vector2(0.78f, 0f), new Vector2(1f, 1f),
                new Color(0.25f, 0.25f, 0.32f, 1f), Close);
        }

        private static void Save()
        {
            if (_downloader != null && _downloader.Running)
            {
                Toast.Warning("Wait for the Scryfall download to finish, or cancel it");
                return;
            }
            CollectionExporter.Run(_profile, _index, result =>
            {
                _last = result;
                if (_frame != null) Redraw();
            });
        }

        // ------------------------------------------------------------------
        // Righe

        private static void FormatRow(Transform list, ExportProfile p)
        {
            bool selected = p == _profile;
            var row = Row(list);
            SetHeight(row, RowHeight * 1.75f);
            row.GetComponent<Image>().color = selected ? new Color(0.22f, 0.33f, 0.48f, 1f) : RowBackground;

            Label(row.transform, (selected ? "● " : "○ ") + p.DisplayName, selected ? Color.white : TextPrimary, 17,
                TextAlignmentOptions.BottomLeft, new Vector2(0f, 0.52f), new Vector2(0.70f, 1f),
                new Vector4(12, 0, 4, 2), FontStyles.Bold);
            Label(row.transform, p.FileName, TextDisabled, 12, TextAlignmentOptions.BottomRight,
                new Vector2(0.55f, 0.52f), new Vector2(1f, 1f), new Vector4(4, 0, 12, 2));
            var desc = Label(row.transform, p.Description, TextSecondary, 13, TextAlignmentOptions.TopLeft,
                new Vector2(0f, 0f), new Vector2(1f, 0.52f), new Vector4(30, 2, 12, 0));
            desc.textWrappingMode = TextWrappingModes.Normal;

            var b = row.AddComponent<UnityEngine.UI.Button>();
            b.transition = Selectable.Transition.None;
            var chosen = p;
            b.onClick.AddListener(() =>
            {
                if (_profile == chosen) return;
                _profile = chosen;
                _last = null;   // il riepilogo era del formato di prima
                Redraw();
            });
        }

        private static void IndexRow(Transform list)
        {
            var row = Row(list);
            SetHeight(row, RowHeight * 1.75f);
            bool running = _downloader != null && _downloader.Running;

            _indexStatus = Label(row.transform, IndexStatusText(), TextPrimary, 14, TextAlignmentOptions.MidlineLeft,
                new Vector2(0f, 0f), new Vector2(0.74f, 1f), new Vector4(12, 0, 6, 0));
            _indexStatus.textWrappingMode = TextWrappingModes.Normal;

            string label = running ? "Cancel" : (_index == null ? "Download" : "Update");
            Button(row.transform, label, new Vector2(0.76f, 0.2f), new Vector2(0.98f, 0.8f),
                running ? ButtonIdle : AccentBlue, () =>
                {
                    if (_downloader != null && _downloader.Running) { _downloader.Cancel(); return; }
                    StartDownload();
                });
        }

        private static string IndexStatusText()
        {
            if (_downloader != null && _downloader.Running) return "Scryfall: " + _downloader.Status;
            string tail = _downloader != null && !_downloader.Succeeded && !string.IsNullOrEmpty(_downloader.Status)
                ? "\nLast download: " + _downloader.Status : "";
            if (_index == null)
                return (_indexError != null ? "Saved Scryfall index is damaged (" + _indexError + ")." : "No Scryfall index yet.")
                       + " Download it once (about a minute and a half)." + tail;
            int days = (int)Math.Floor((DateTime.UtcNow - _index.BuiltUtc).TotalDays);
            string age = days <= 0 ? "today" : days == 1 ? "yesterday" : days + " days ago";
            return $"Scryfall index: {_index.Count:N0} Arena printings, downloaded {age}. " +
                   "Update it after a new Arena set comes out." + tail;
        }

        private static void StartDownload()
        {
            var host = PluginBehaviour.Instance;
            if (host == null) { Toast.Error("Plugin not ready"); return; }
            _downloader = new ScryfallIndexDownloader(IndexPath, $"MTGAPlus/{PluginInfo.VERSION} (collection export)");
            _downloader.Start();
            host.StartCoroutine(WatchDownload(_downloader));
            Redraw();
        }

        private static IEnumerator WatchDownload(ScryfallIndexDownloader d)
        {
            while (d.Running)
            {
                if (_indexStatus != null) _indexStatus.text = IndexStatusText();
                yield return new WaitForSeconds(0.5f);
            }
            if (d.Succeeded && d.Result != null)
            {
                _index = d.Result;
                _indexError = null;
                Toast.Success($"Scryfall index ready: {_index.Count:N0} printings");
            }
            else
            {
                Toast.Warning("Scryfall download: " + d.Status);
            }
            Plugin.Log.LogInfo("CollectionExport: Scryfall download ended — " + d.Status);
            if (_frame != null && d == _downloader) Redraw();
        }

        // ------------------------------------------------------------------
        // Riepilogo dopo il salvataggio

        private static void Summary(Transform list, CollectionExporter.Result r)
        {
            var m = r.Model;
            var o = r.Output;
            Spacer(list);
            Heading(list, "Last export: " + Path.GetFileName(r.Path));

            Text(list, $"{o.CopiesWritten:N0} copies on {o.DataLines:N0} lines, out of {m.CopiesIn:N0} owned.", TextPrimary, 15, 1f);
            Text(list, $"Exact printing (Arena id): {m.CopiesWith(MatchPrecision.ArenaId):N0}   ·   " +
                       $"by set and number: {m.CopiesWith(MatchPrecision.SetAndNumber):N0}   ·   " +
                       $"name only: {m.CopiesWith(MatchPrecision.NameOnly):N0}", TextSecondary, 14, 1f);

            int rejected = o.Rejected.Sum(x => x.Quantity);
            if (rejected > 0)
                Text(list, $"Not in this file: {rejected:N0} copies — {o.RejectReason}. " +
                           "Moxfield / Archidekt and Generic CSV keep them.", LossRed, 14, 1.6f);
            if (o.CopiesWithCaveat > 0)
                Text(list, $"{o.CopiesWithCaveat:N0} copies with a caveat: {o.CaveatReason}.", TextSecondary, 14, 1f);
            if (m.CopiesDigitalOnly > 0)
                Text(list, $"{m.CopiesDigitalOnly:N0} copies are digital-only printings (Arena, Alchemy). " +
                           "Sites built for paper cards may not accept them.", TextSecondary, 14, 1.6f);
            if (m.CopiesSkipped > 0)
                Text(list, $"Not exportable: {m.CopiesSkipped:N0} copies (tokens and cards Arena can't describe; list in the log).",
                     TextSecondary, 14, 1f);

            // Le righe per nome, con il motivo: sono quelle che l'utente
            // potrebbe dover sistemare a mano sul sito.
            var partial = m.Rows.Where(x => x.Precision == MatchPrecision.NameOnly).ToList();
            if (partial.Count > 0)
            {
                Heading(list, $"Exported by name only ({partial.Count:N0})");
                foreach (var x in partial.Take(12))
                {
                    var t = Text(list, $"{x.Quantity}× {x.Name}  [{x.ArenaSetCode} {x.ArenaCollectorNumber}] — {x.Note}",
                                 TextDisabled, 13, 1f);
                    t.richText = false;
                }
                if (partial.Count > 12)
                    Text(list, $"…and {partial.Count - 12:N0} more. The Generic CSV lists them all, with the reason.", TextDisabled, 13, 1f);
            }
        }

        // ------------------------------------------------------------------
        // Pezzi

        private static void Heading(Transform list, string text)
        {
            var t = Text(list, text, Color.white, 16, 1f);
            t.fontStyle = FontStyles.Bold;
        }

        private static TextMeshProUGUI Text(Transform list, string text, Color color, float size, float heightRows)
        {
            var go = NewChild(list, "Text");
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = RowHeight * heightRows;
            le.minHeight = RowHeight * heightRows;
            var t = Label(go.transform, text, color, size, TextAlignmentOptions.MidlineLeft,
                Vector2.zero, Vector2.one, new Vector4(12, 0, 12, 0));
            t.textWrappingMode = TextWrappingModes.Normal;
            return t;
        }

        private static void SetHeight(GameObject row, float h)
        {
            var le = row.GetComponent<LayoutElement>();
            le.preferredHeight = h;
            le.minHeight = h;
        }
    }
}
