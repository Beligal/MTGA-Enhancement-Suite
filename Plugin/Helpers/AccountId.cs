using System.Reflection;
using HarmonyLib;

namespace MTGAEnhancementSuite.Helpers
{
    /// <summary>
    /// L'identificativo dell'account a cui si e' loggati
    /// (<c>Pantry.Get&lt;IAccountClient&gt;().AccountInformation.PersonaID</c>).
    ///
    /// Serve a tutto cio' che e' "di questo account e non dell'altro": le
    /// cartelle dei mazzi lo usavano gia', e le statistiche delle partite
    /// devono fare lo stesso — un utente con due account non deve trovarsi i
    /// numeri mescolati.
    ///
    /// Estratto da <c>DeckOrganizationManager</c> il 2026-09-20, dov'era una
    /// catena di reflection privata. Averne due copie da tenere allineate era
    /// il modo piu' rapido per farle divergere: `IAccountClient` sta in
    /// un'assembly che non referenziamo, quindi si risolve per nome, e il
    /// giorno che Arena rinomina qualcosa il punto da correggere dev'essere
    /// uno solo.
    /// </summary>
    internal static class AccountId
    {
        private static MethodInfo _pantryGetAccountClient;
        private static PropertyInfo _accountInfoProp;
        private static FieldInfo _personaIdField;

        /// <summary>
        /// Il PersonaID corrente, o null se il login non e' ancora arrivato
        /// fin qui. Chi chiama deve reggere il null: al primo avvio il Pantry
        /// non ha ancora l'account.
        /// </summary>
        public static string Current()
        {
            try
            {
                if (_pantryGetAccountClient == null)
                {
                    var pantryType = GameReflection.TypeByName("Pantry");
                    var accountClientType = GameReflection.TypeByName("IAccountClient");
                    if (pantryType == null || accountClientType == null) return null;
                    _pantryGetAccountClient = pantryType.GetMethod("Get")
                        ?.MakeGenericMethod(accountClientType);
                    _accountInfoProp = AccessTools.Property(accountClientType, "AccountInformation");
                }

                var accountClient = _pantryGetAccountClient?.Invoke(null, null);
                if (accountClient == null) return null;

                var info = _accountInfoProp?.GetValue(accountClient);
                if (info == null) return null;

                if (_personaIdField == null)
                    _personaIdField = AccessTools.Field(info.GetType(), "PersonaID");

                return _personaIdField?.GetValue(info) as string;
            }
            catch { return null; }
        }

        /// <summary>
        /// Lo stesso valore ripulito per finire in un nome di cartella. Null
        /// se l'account non e' noto: chi salva deve aspettare, non inventarsi
        /// una cartella "unknown" in cui poi mescolare account diversi.
        /// </summary>
        public static string CurrentAsFolderName()
        {
            var id = Current();
            if (string.IsNullOrEmpty(id)) return null;

            var chars = id.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                          || (c >= '0' && c <= '9') || c == '-' || c == '_';
                if (!ok) chars[i] = '_';
            }
            return new string(chars);
        }
    }
}
