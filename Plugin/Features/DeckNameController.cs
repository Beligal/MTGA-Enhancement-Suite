using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using MTGAEnhancementSuite.Helpers;
using MTGAEnhancementSuite.Patches;
using MTGAEnhancementSuite.UI;
using Wizards.Mtga.Decks;

namespace MTGAEnhancementSuite.Features
{
    /// <summary>
    /// I due modi di arrivare all'editor del nome, e i due modi di salvarlo.
    ///
    ///  - **Dal deck builder** il nome si scrive nel modello con
    ///    <c>DeckBuilderModelProvider.SetDeckName</c> e parte con il normale
    ///    salvataggio del mazzo: nessuna richiesta di rete in piu', e se non
    ///    salvi il mazzo non cambia niente.
    ///  - **Dalla lista dei mazzi** non c'e' un modello aperto, quindi il nome
    ///    va scritto sul mazzo e mandato al server con
    ///    <c>DecksManager.UpdateDeck</c> — la stessa strada che percorre Arena
    ///    quando rinomini un mazzo dal riquadro dei dettagli
    ///    (<c>DeckManagerController.Coroutine_UpdateDeckName</c>).
    ///
    /// <c>UpdateDeck</c> restituisce una <c>Promise&lt;T&gt;</c> da un'assembly
    /// non referenziata, quindi va invocata per reflection e attesa con
    /// <see cref="GameReflection.AwaitPromise"/>, come gia' fa
    /// <see cref="LocalDeckConverter"/>. Anche <c>DeckActionType</c> si ricava
    /// dal parametro del metodo invece di referenziare un'altra assembly per un
    /// solo valore.
    /// </summary>
    internal static class DeckNameController
    {
        private static MethodInfo _updateDeck;
        private static object _actionUpdated;

        // -----------------------------------------------------------------
        // Dal deck builder
        // -----------------------------------------------------------------
        public static void OpenForOpenDeck()
        {
            var provider = GameReflection.PantryGet<Core.Code.Decks.DeckBuilderModelProvider>();
            if (provider == null || provider.Model == null || !provider.Model.HasLoadedDeck)
            {
                Toast.Warning("Deck name: no deck open");
                return;
            }

            string current = provider.Model._deckName ?? "";

            DeckNameStudio.Show(current, "editing the open deck", generated =>
            {
                var p = GameReflection.PantryGet<Core.Code.Decks.DeckBuilderModelProvider>();
                if (p == null || p.Model == null || !p.Model.HasLoadedDeck)
                {
                    Toast.Error("Deck name: the deck was closed");
                    return;
                }

                try
                {
                    p.SetDeckName(generated);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"DeckName: SetDeckName fallita: {ex}");
                    Toast.Error("Couldn't set the deck name");
                    return;
                }

                Plugin.Log.LogInfo($"DeckName: nome del mazzo aperto impostato a '{generated}'.");
                PerPlayerLog.Info($"Deck name styled (builder): {generated}");

                // SetDeckName scrive solo nel modello: il nome parte per il
                // server quando salvi il mazzo, come qualunque altra modifica
                // fatta qui dentro. Dirlo evita di andare a cercare perche' in
                // lista si vede ancora quello vecchio.
                Toast.Success("Name set - save the deck to keep it", 5f);
            });
        }

        // -----------------------------------------------------------------
        // Dalla lista dei mazzi
        // -----------------------------------------------------------------
        public static void OpenForDeck(Guid deckId)
        {
            if (deckId == Guid.Empty) return;

            var dm = GameReflection.PantryGet<DecksManager>();
            if (dm == null) { Toast.Warning("Deck name: deck service not available"); return; }

            Client_Deck deck;
            try { deck = dm.GetDeck(deckId); }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"DeckName: GetDeck({deckId}) fallita: {ex.Message}");
                return;
            }

            if (deck?.Summary == null) { Toast.Warning("Deck name: deck not found"); return; }

            if (deck.Summary.IsNetDeck)
            {
                // I precostruiti del gioco non sono nostri da rinominare: il
                // server rifiuterebbe, e tanto vale dirlo prima.
                Toast.Warning("Deck name: preconstructed decks can't be renamed");
                return;
            }

            DeckNameStudio.Show(deck.Summary.Name ?? "", "renaming from the deck list", generated =>
            {
                var ctrl = LocalDeckBridge.Controller;
                if (ctrl == null) { Toast.Error("Deck name: deck manager not available"); return; }
                ctrl.StartCoroutine(RenameCoroutine(deckId, generated));
            });
        }

        private static IEnumerator RenameCoroutine(Guid deckId, string newName)
        {
            EnsureReflection();

            var dm = GameReflection.PantryGet<DecksManager>();
            if (dm == null || _updateDeck == null || _actionUpdated == null)
            {
                Toast.Error("Couldn't reach the deck service");
                yield break;
            }

            Client_Deck deck = null;
            try { deck = dm.GetDeck(deckId); } catch { }
            if (deck?.Summary == null) { Toast.Error("Deck not found"); yield break; }

            string oldName = deck.Summary.Name;
            if (string.Equals(oldName, newName, StringComparison.Ordinal))
            {
                Toast.Info("The name is unchanged");
                yield break;
            }

            // Arena confronta i nomi sul testo GREZZO, tag compresi: due mazzi
            // che a schermo si leggono uguali ma hanno colori diversi passano
            // entrambi. Il controllo lo facciamo comunque, perche' un duplicato
            // esatto lo rifiuterebbe il server.
            bool duplicate = false;
            try { duplicate = dm.DeckNameAlreadyExists(deckId, newName); } catch { }
            if (duplicate)
            {
                Toast.Warning("Another deck already has exactly this name");
                yield break;
            }

            object promise = null;
            // Il nome va scritto PRIMA della chiamata: UpdateDeck manda il mazzo
            // cosi' com'e'. Se la chiamata fallisce lo rimettiamo com'era.
            deck.Summary.Name = newName;
            try
            {
                promise = _updateDeck.Invoke(dm, new object[] { deck, _actionUpdated });
            }
            catch (Exception ex)
            {
                deck.Summary.Name = oldName;
                Plugin.Log.LogError($"DeckName: UpdateDeck ha sollevato: {ex}");
                Toast.Error("Couldn't rename the deck");
                yield break;
            }

            if (promise == null)
            {
                deck.Summary.Name = oldName;
                Toast.Error("Couldn't rename the deck");
                yield break;
            }

            bool ok = false;
            yield return GameReflection.AwaitPromise(promise, (success, _) => { ok = success; });

            if (!ok)
            {
                deck.Summary.Name = oldName;
                Plugin.Log.LogWarning($"DeckName: il server ha rifiutato il rinomina di {deckId}.");
                Toast.Error("The server refused the new name - nothing changed");
                yield break;
            }

            Plugin.Log.LogInfo($"DeckName: mazzo {deckId} rinominato in '{newName}'.");
            PerPlayerLog.Info($"Deck renamed: '{oldName}' -> '{newName}'");

            DeckManagerControllerPatch.ReloadDecksLikeMTGADoes();
            Toast.Success("Deck renamed");
        }

        private static void EnsureReflection()
        {
            if (_updateDeck != null) return;

            _updateDeck = AccessTools.Method(typeof(DecksManager), "UpdateDeck");
            if (_updateDeck == null)
            {
                Plugin.Log.LogWarning("DeckName: DecksManager.UpdateDeck non trovato.");
                return;
            }

            // Il secondo parametro e' DeckActionType, in un'assembly che non
            // referenziamo: prendiamo il tipo dalla firma e il valore per nome,
            // invece di aggiungere un riferimento per una sola costante.
            var pars = _updateDeck.GetParameters();
            if (pars.Length < 2 || !pars[1].ParameterType.IsEnum)
            {
                Plugin.Log.LogWarning("DeckName: firma di UpdateDeck inattesa, rinomina disattivato.");
                _updateDeck = null;
                return;
            }

            try
            {
                _actionUpdated = Enum.Parse(pars[1].ParameterType, "Updated");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"DeckName: DeckActionType.Updated non risolto ({ex.Message}).");
                _updateDeck = null;
            }
        }
    }
}
