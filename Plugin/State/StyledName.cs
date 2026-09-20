using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace MTGAEnhancementSuite.State
{
    /// <summary>Maiuscole/minuscole: i tre tag si escludono a vicenda.</summary>
    internal enum CaseMode { None = 0, SmallCaps = 1, Upper = 2, Lower = 3 }

    /// <summary>
    /// Lo stile di UN carattere. L'editor tiene un array di questi lungo quanto
    /// il nome in chiaro; i tag non esistono finche' non si genera la stringa.
    ///
    /// Solo attributi che TMP sa rendere da solo: niente &lt;sprite&gt;,
    /// &lt;font&gt;, &lt;material&gt;, &lt;gradient&gt; o &lt;style&gt;, che
    /// cercano un asset per nome nelle Resources del gioco e, se non lo trovano,
    /// fanno stampare il tag come testo (verificato su TMP_Text.ValidateHtmlTag,
    /// 2026-09-19).
    /// </summary>
    internal struct CharStyle : IEquatable<CharStyle>
    {
        public bool HasColor;
        public Color32 Color;

        public bool HasMark;
        public Color32 Mark;

        public bool Bold;
        public bool Italic;
        public bool Underline;
        public bool Strike;

        public CaseMode Case;

        /// <summary>Dimensione in percentuale. 0 = quella di default.</summary>
        public int SizePercent;

        /// <summary>Alzata/abbassata, in decimi di em. 0 = nessuna.</summary>
        public int VOffsetTenths;

        /// <summary>Spaziatura fra le lettere, in decimi di em. 0 = nessuna.</summary>
        public int CSpaceTenths;

        /// <summary>Rotazione in gradi. 0 = nessuna.</summary>
        public int RotateDegrees;

        public bool IsPlain =>
            !HasColor && !HasMark && !Bold && !Italic && !Underline && !Strike &&
            Case == CaseMode.None && SizePercent == 0 && VOffsetTenths == 0 &&
            CSpaceTenths == 0 && RotateDegrees == 0;

        public bool Equals(CharStyle o)
        {
            return HasColor == o.HasColor && SameColor(Color, o.Color, HasColor)
                && HasMark == o.HasMark && SameColor(Mark, o.Mark, HasMark)
                && Bold == o.Bold && Italic == o.Italic
                && Underline == o.Underline && Strike == o.Strike
                && Case == o.Case && SizePercent == o.SizePercent
                && VOffsetTenths == o.VOffsetTenths && CSpaceTenths == o.CSpaceTenths
                && RotateDegrees == o.RotateDegrees;
        }

        public override bool Equals(object obj) => obj is CharStyle s && Equals(s);

        public override int GetHashCode()
        {
            int h = 17;
            h = h * 31 + (HasColor ? ColorKey(Color) : -1);
            h = h * 31 + (HasMark ? ColorKey(Mark) : -1);
            h = h * 31 + (Bold ? 1 : 0) + (Italic ? 2 : 0) + (Underline ? 4 : 0) + (Strike ? 8 : 0);
            h = h * 31 + (int)Case;
            h = h * 31 + SizePercent;
            h = h * 31 + VOffsetTenths;
            h = h * 31 + CSpaceTenths;
            h = h * 31 + RotateDegrees;
            return h;
        }

        private static int ColorKey(Color32 c) => (c.r << 24) | (c.g << 16) | (c.b << 8) | c.a;

        private static bool SameColor(Color32 a, Color32 b, bool relevant)
        {
            if (!relevant) return true;
            return a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;
        }
    }

    /// <summary>
    /// Conversione fra "nome in chiaro + stile per carattere" e la stringa con i
    /// tag che Arena salva davvero.
    ///
    /// **Il budget e' 64 caratteri, tag compresi**
    /// (<c>DeckValidationUtils.DECK_NAME_MAX_LENGTH</c>), e il generatore e'
    /// scritto tutto intorno a questo:
    ///
    ///  - **non chiude mai un tag a fine stringa.** TMP azzera lo stato a ogni
    ///    etichetta, quindi un &lt;color&gt; lasciato aperto e' legittimo e fa
    ///    risparmiare 8 caratteri su 64. Non e' sciatteria: e' un ottavo del
    ///    budget.
    ///  - **sceglie la forma piu' corta del colore.** Il parser di TMP accetta
    ///    #RGB, #RGBA, #RRGGBB e #RRGGBBAA (verificato su TMP_Text, che controlla
    ///    la lunghezza del tag: 10, 11, 13, 15), piu' dieci colori per nome.
    ///    Quindi rosso pieno si scrive &lt;color=red&gt;, 11 caratteri invece di
    ///    19.
    ///
    /// **Attenzione ai valori dei colori per nome:** TMP usa le costanti di
    /// Unity, e <c>Color.yellow</c> **non e' FFFF00** ma (255,235,4). Stessa
    /// cosa per orange (255,128,0) e purple (160,32,240). I valori qui sotto
    /// sono copiati dal parser decompilato, non indovinati: se non combaciassero
    /// esattamente, useremmo il nome per un colore che l'utente non ha scelto.
    /// </summary>
    internal static class StyledName
    {
        /// <summary>Come <c>DeckValidationUtils.DECK_NAME_MAX_LENGTH</c>.</summary>
        public const int MaxLength = 64;

        /// <summary>
        /// "C'era un colore, ed era questo" / "non c'era". Serve una coppia
        /// esplicita e non un valore sentinella: (0,0,0,0) e' nero trasparente,
        /// un colore che l'utente puo' davvero scegliere, e usarlo per dire
        /// "nessun colore" vorrebbe dire perderlo alla prima chiusura di tag.
        /// </summary>
        private struct ColorSlot
        {
            public bool Has;
            public Color32 Value;
        }

        private struct NamedColor
        {
            public readonly string Name;
            public readonly byte R, G, B;
            public NamedColor(string name, byte r, byte g, byte b) { Name = name; R = r; G = g; B = b; }
        }

        // Copiati uno per uno dallo switch di TMP_Text.ValidateHtmlTag.
        private static readonly NamedColor[] Named =
        {
            new NamedColor("red",       255, 0,   0),
            new NamedColor("blue",      0,   0,   255),
            new NamedColor("green",     0,   255, 0),
            new NamedColor("white",     255, 255, 255),
            new NamedColor("black",     0,   0,   0),
            new NamedColor("grey",      128, 128, 128),
            new NamedColor("orange",    255, 128, 0),
            new NamedColor("purple",    160, 32,  240),
            new NamedColor("yellow",    255, 235, 4),
            new NamedColor("lightblue", 173, 216, 230),
        };

        // -----------------------------------------------------------------
        // Generazione
        // -----------------------------------------------------------------

        /// <summary>
        /// Il nome con i tag, pronto da salvare. <paramref name="styles"/> deve
        /// essere lungo quanto <paramref name="plain"/>; se e' piu' corto i
        /// caratteri in eccesso valgono "nessuno stile".
        /// </summary>
        public static string Generate(string plain, IList<CharStyle> styles)
        {
            if (string.IsNullOrEmpty(plain)) return plain ?? "";

            var sb = new StringBuilder(plain.Length + 24);
            var open = new CharStyle();     // stile attualmente aperto nei tag

            for (int i = 0; i < plain.Length; i++)
            {
                var want = (styles != null && i < styles.Count) ? styles[i] : new CharStyle();

                if (!want.Equals(open))
                {
                    CloseDiff(sb, open, want);
                    OpenDiff(sb, open, want);
                    open = want;
                }

                // Un '<' nel nome farebbe partire il parser di TMP su testo che
                // non e' un tag. TMP lo stampa letterale quando il tag non e'
                // valido, quindi non rompe niente, ma <noparse> costerebbe 19
                // caratteri: lasciamo passare e ci pensa il controllo in UI.
                sb.Append(plain[i]);
            }

            // Niente tag di chiusura in coda, di proposito: vedi il commento di
            // classe. TMP riparte pulito a ogni etichetta.
            return sb.ToString();
        }

        /// <summary>Chiude cio' che c'era e nel nuovo stile non c'e' piu'.</summary>
        private static void CloseDiff(StringBuilder sb, CharStyle from, CharStyle to)
        {
            if (from.RotateDegrees != 0 && to.RotateDegrees != from.RotateDegrees) sb.Append("</rotate>");
            if (from.CSpaceTenths != 0 && to.CSpaceTenths != from.CSpaceTenths) sb.Append("</cspace>");
            if (from.VOffsetTenths != 0 && to.VOffsetTenths != from.VOffsetTenths) sb.Append("</voffset>");
            if (from.SizePercent != 0 && to.SizePercent != from.SizePercent) sb.Append("</size>");
            if (from.Case != CaseMode.None && to.Case != from.Case) sb.Append(CaseCloseTag(from.Case));
            if (from.Strike && !to.Strike) sb.Append("</s>");
            if (from.Underline && !to.Underline) sb.Append("</u>");
            if (from.Italic && !to.Italic) sb.Append("</i>");
            if (from.Bold && !to.Bold) sb.Append("</b>");
            if (from.HasMark && !(to.HasMark && SameRgba(from.Mark, to.Mark))) sb.Append("</mark>");
            if (from.HasColor && !(to.HasColor && SameRgba(from.Color, to.Color))) sb.Append("</color>");
        }

        /// <summary>Apre cio' che il nuovo stile vuole e non era gia' aperto.</summary>
        private static void OpenDiff(StringBuilder sb, CharStyle from, CharStyle to)
        {
            if (to.HasColor && !(from.HasColor && SameRgba(from.Color, to.Color)))
                sb.Append("<color=").Append(ShortestColor(to.Color)).Append('>');

            if (to.HasMark && !(from.HasMark && SameRgba(from.Mark, to.Mark)))
                sb.Append("<mark=").Append(HexRgba(to.Mark)).Append('>');

            if (to.Bold && !from.Bold) sb.Append("<b>");
            if (to.Italic && !from.Italic) sb.Append("<i>");
            if (to.Underline && !from.Underline) sb.Append("<u>");
            if (to.Strike && !from.Strike) sb.Append("<s>");

            if (to.Case != CaseMode.None && to.Case != from.Case) sb.Append(CaseOpenTag(to.Case));

            if (to.SizePercent != 0 && to.SizePercent != from.SizePercent)
                sb.Append("<size=").Append(to.SizePercent.ToString(CultureInfo.InvariantCulture)).Append("%>");

            if (to.VOffsetTenths != 0 && to.VOffsetTenths != from.VOffsetTenths)
                sb.Append("<voffset=").Append(Tenths(to.VOffsetTenths)).Append("em>");

            if (to.CSpaceTenths != 0 && to.CSpaceTenths != from.CSpaceTenths)
                sb.Append("<cspace=").Append(Tenths(to.CSpaceTenths)).Append("em>");

            if (to.RotateDegrees != 0 && to.RotateDegrees != from.RotateDegrees)
                sb.Append("<rotate=").Append(to.RotateDegrees.ToString(CultureInfo.InvariantCulture)).Append('>');
        }

        private static string CaseOpenTag(CaseMode c)
        {
            switch (c)
            {
                case CaseMode.SmallCaps: return "<smallcaps>";
                case CaseMode.Upper: return "<uppercase>";
                case CaseMode.Lower: return "<lowercase>";
                default: return "";
            }
        }

        private static string CaseCloseTag(CaseMode c)
        {
            switch (c)
            {
                case CaseMode.SmallCaps: return "</smallcaps>";
                case CaseMode.Upper: return "</uppercase>";
                case CaseMode.Lower: return "</lowercase>";
                default: return "";
            }
        }

        /// <summary>"3" da 30 decimi, "0.5" da 5. Niente zeri inutili: costano.</summary>
        private static string Tenths(int tenths)
        {
            int whole = tenths / 10;
            int frac = Math.Abs(tenths % 10);
            if (frac == 0) return whole.ToString(CultureInfo.InvariantCulture);
            string sign = (tenths < 0 && whole == 0) ? "-" : "";
            return sign + whole.ToString(CultureInfo.InvariantCulture) + "." +
                   frac.ToString(CultureInfo.InvariantCulture);
        }

        // -----------------------------------------------------------------
        // Colori
        // -----------------------------------------------------------------

        /// <summary>
        /// La scrittura piu' corta per questo colore: nome se ce n'e' uno esatto,
        /// altrimenti #RGB quando ogni canale ha i due mezzi byte uguali,
        /// altrimenti #RRGGBB. L'alfa si scrive solo se non e' pieno.
        /// </summary>
        public static string ShortestColor(Color32 c)
        {
            if (c.a == 255)
            {
                foreach (var n in Named)
                    if (n.R == c.r && n.G == c.g && n.B == c.b) return n.Name;

                if (IsNibbleRepeat(c.r) && IsNibbleRepeat(c.g) && IsNibbleRepeat(c.b))
                    return "#" + Nibble(c.r) + Nibble(c.g) + Nibble(c.b);

                return "#" + Byte2(c.r) + Byte2(c.g) + Byte2(c.b);
            }

            if (IsNibbleRepeat(c.r) && IsNibbleRepeat(c.g) && IsNibbleRepeat(c.b) && IsNibbleRepeat(c.a))
                return "#" + Nibble(c.r) + Nibble(c.g) + Nibble(c.b) + Nibble(c.a);

            return "#" + Byte2(c.r) + Byte2(c.g) + Byte2(c.b) + Byte2(c.a);
        }

        /// <summary>&lt;mark&gt; vuole sempre l'alfa: senza, evidenzia coprendo il testo.</summary>
        public static string HexRgba(Color32 c) =>
            "#" + Byte2(c.r) + Byte2(c.g) + Byte2(c.b) + Byte2(c.a);

        private static bool IsNibbleRepeat(byte b) => (b >> 4) == (b & 0x0F);
        private static string Nibble(byte b) => (b >> 4).ToString("X1", CultureInfo.InvariantCulture);
        private static string Byte2(byte b) => b.ToString("X2", CultureInfo.InvariantCulture);
        private static bool SameRgba(Color32 a, Color32 b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;

        /// <summary>
        /// Legge "#RGB", "#RGBA", "#RRGGBB", "#RRGGBBAA" o un nome fra i dieci.
        /// Gli stessi quattro formati che accetta TMP, ne' uno di piu'.
        /// </summary>
        public static bool TryParseColor(string text, out Color32 color)
        {
            color = new Color32(255, 255, 255, 255);
            if (string.IsNullOrEmpty(text)) return false;
            text = text.Trim();

            foreach (var n in Named)
                if (string.Equals(text, n.Name, StringComparison.OrdinalIgnoreCase))
                {
                    color = new Color32(n.R, n.G, n.B, 255);
                    return true;
                }

            if (text[0] != '#') text = "#" + text;
            int len = text.Length - 1;
            if (len != 3 && len != 4 && len != 6 && len != 8) return false;

            for (int i = 1; i < text.Length; i++)
                if (HexVal(text[i]) < 0) return false;

            byte r, g, b, a = 255;
            if (len == 3 || len == 4)
            {
                r = (byte)(HexVal(text[1]) * 17);
                g = (byte)(HexVal(text[2]) * 17);
                b = (byte)(HexVal(text[3]) * 17);
                if (len == 4) a = (byte)(HexVal(text[4]) * 17);
            }
            else
            {
                r = (byte)(HexVal(text[1]) * 16 + HexVal(text[2]));
                g = (byte)(HexVal(text[3]) * 16 + HexVal(text[4]));
                b = (byte)(HexVal(text[5]) * 16 + HexVal(text[6]));
                if (len == 8) a = (byte)(HexVal(text[7]) * 16 + HexVal(text[8]));
            }

            color = new Color32(r, g, b, a);
            return true;
        }

        private static int HexVal(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        // -----------------------------------------------------------------
        // Lettura di un nome gia' esistente
        // -----------------------------------------------------------------

        /// <summary>
        /// Riporta una stringa gia' salvata a "testo in chiaro + stile per
        /// carattere", cosi' riaprire un mazzo gia' colorato mostra i colori e
        /// non i tag.
        ///
        /// Riconosce **solo i tag che questo file genera**. Davanti a
        /// qualunque altra cosa — un tag che non conosce, uno malformato, una
        /// chiusura spaiata — restituisce false: chi chiama passa alla modifica
        /// grezza, che mostra la stringa com'e'. Fingere di aver capito un tag
        /// e poi rigenerarlo diverso vorrebbe dire rovinare in silenzio un nome
        /// che all'utente andava bene.
        /// </summary>
        public static bool TryParse(string raw, out string plain, out List<CharStyle> styles)
        {
            plain = raw ?? "";
            styles = new List<CharStyle>();
            if (string.IsNullOrEmpty(raw)) return true;

            var text = new StringBuilder(raw.Length);
            var cur = new CharStyle();

            // Pile: ogni apertura ricorda cosa ripristinare alla chiusura.
            var colorStack = new List<ColorSlot>();
            var markStack = new List<ColorSlot>();
            var sizeStack = new List<int>();
            var voffStack = new List<int>();
            var cspaceStack = new List<int>();
            var rotateStack = new List<int>();
            var caseStack = new List<CaseMode>();
            int boldDepth = 0, italicDepth = 0, underDepth = 0, strikeDepth = 0;

            int i = 0;
            while (i < raw.Length)
            {
                char ch = raw[i];
                if (ch != '<')
                {
                    text.Append(ch);
                    styles.Add(cur);
                    i++;
                    continue;
                }

                int close = raw.IndexOf('>', i + 1);
                if (close < 0) return false;                       // '<' senza '>'
                string tag = raw.Substring(i + 1, close - i - 1);
                if (tag.Length == 0) return false;

                if (!ApplyTag(tag, ref cur, colorStack, markStack, sizeStack, voffStack,
                              cspaceStack, rotateStack, caseStack,
                              ref boldDepth, ref italicDepth, ref underDepth, ref strikeDepth))
                    return false;

                i = close + 1;
            }

            plain = text.ToString();
            return true;
        }

        private static bool ApplyTag(string tag, ref CharStyle cur,
                                     List<ColorSlot> colorStack, List<ColorSlot> markStack,
                                     List<int> sizeStack, List<int> voffStack,
                                     List<int> cspaceStack, List<int> rotateStack,
                                     List<CaseMode> caseStack,
                                     ref int boldDepth, ref int italicDepth,
                                     ref int underDepth, ref int strikeDepth)
        {
            bool closing = tag[0] == '/';
            string body = closing ? tag.Substring(1) : tag;
            string name = body, value = null;
            int eq = body.IndexOf('=');
            if (eq >= 0) { name = body.Substring(0, eq); value = body.Substring(eq + 1); }
            name = name.ToLowerInvariant();

            switch (name)
            {
                case "color":
                {
                    if (closing)
                    {
                        var prev = Pop(colorStack);
                        cur.HasColor = prev.Has; cur.Color = prev.Value;
                        return true;
                    }
                    if (value == null || !TryParseColor(value, out var col)) return false;
                    colorStack.Add(new ColorSlot { Has = cur.HasColor, Value = cur.Color });
                    cur.HasColor = true; cur.Color = col;
                    return true;
                }

                case "mark":
                {
                    if (closing)
                    {
                        var prev = Pop(markStack);
                        cur.HasMark = prev.Has; cur.Mark = prev.Value;
                        return true;
                    }
                    if (value == null || !TryParseColor(value, out var mk)) return false;
                    markStack.Add(new ColorSlot { Has = cur.HasMark, Value = cur.Mark });
                    cur.HasMark = true; cur.Mark = mk;
                    return true;
                }

                case "b": return Toggle(closing, ref boldDepth, ref cur.Bold);
                case "i": return Toggle(closing, ref italicDepth, ref cur.Italic);
                case "u": return Toggle(closing, ref underDepth, ref cur.Underline);
                case "s": return Toggle(closing, ref strikeDepth, ref cur.Strike);

                case "smallcaps": return Case(closing, CaseMode.SmallCaps, ref cur, caseStack);
                case "uppercase": return Case(closing, CaseMode.Upper, ref cur, caseStack);
                case "lowercase": return Case(closing, CaseMode.Lower, ref cur, caseStack);

                case "size":
                    if (closing) { Pop(sizeStack, out cur.SizePercent); return true; }
                    if (value == null || !value.EndsWith("%", StringComparison.Ordinal)) return false;
                    if (!int.TryParse(value.Substring(0, value.Length - 1),
                                      NumberStyles.Integer, CultureInfo.InvariantCulture, out int pct)) return false;
                    sizeStack.Add(cur.SizePercent);
                    cur.SizePercent = pct;
                    return true;

                case "voffset":
                    if (closing) { Pop(voffStack, out cur.VOffsetTenths); return true; }
                    if (!TryParseEm(value, out int vo)) return false;
                    voffStack.Add(cur.VOffsetTenths);
                    cur.VOffsetTenths = vo;
                    return true;

                case "cspace":
                    if (closing) { Pop(cspaceStack, out cur.CSpaceTenths); return true; }
                    if (!TryParseEm(value, out int cs)) return false;
                    cspaceStack.Add(cur.CSpaceTenths);
                    cur.CSpaceTenths = cs;
                    return true;

                case "rotate":
                    if (closing) { Pop(rotateStack, out cur.RotateDegrees); return true; }
                    if (value == null || !int.TryParse(value, NumberStyles.Integer,
                                                       CultureInfo.InvariantCulture, out int deg)) return false;
                    rotateStack.Add(cur.RotateDegrees);
                    cur.RotateDegrees = deg;
                    return true;

                default:
                    // Un tag che non generiamo noi. Non proviamo a indovinarlo.
                    return false;
            }
        }

        private static bool TryParseEm(string value, out int tenths)
        {
            tenths = 0;
            if (value == null || !value.EndsWith("em", StringComparison.OrdinalIgnoreCase)) return false;
            string num = value.Substring(0, value.Length - 2);
            if (!float.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) return false;
            tenths = Mathf.RoundToInt(f * 10f);
            return true;
        }

        private static bool Toggle(bool closing, ref int depth, ref bool flag)
        {
            if (closing)
            {
                if (depth == 0) return false;       // chiusura spaiata
                depth--;
                flag = depth > 0;
                return true;
            }
            depth++;
            flag = true;
            return true;
        }

        private static bool Case(bool closing, CaseMode mode, ref CharStyle cur, List<CaseMode> stack)
        {
            if (closing)
            {
                if (cur.Case != mode) return false;
                CaseMode prev;
                Pop(stack, out prev);
                cur.Case = prev;
                return true;
            }
            stack.Add(cur.Case);
            cur.Case = mode;
            return true;
        }

        private static ColorSlot Pop(List<ColorSlot> stack)
        {
            if (stack.Count == 0) return new ColorSlot();
            var value = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            return value;
        }

        private static void Pop(List<int> stack, out int value)
        {
            if (stack.Count == 0) { value = 0; return; }
            value = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
        }

        private static void Pop(List<CaseMode> stack, out CaseMode value)
        {
            if (stack.Count == 0) { value = CaseMode.None; return; }
            value = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
        }

        // -----------------------------------------------------------------
        // Controllo di sicurezza
        // -----------------------------------------------------------------

        /// <summary>
        /// Genera, rilegge quello che ha generato, e confronta. Se il giro non
        /// torna vuol dire che generatore e lettore hanno idee diverse sugli
        /// stessi tag, e il prossimo a rimetterci sarebbe il nome di un mazzo
        /// dell'utente. Costa pochi microsecondi su 64 caratteri e gira solo
        /// quando si preme Applica.
        /// </summary>
        public static bool RoundTrips(string plain, IList<CharStyle> styles, string generated)
        {
            if (!TryParse(generated, out string backPlain, out var backStyles)) return false;
            if (!string.Equals(backPlain, plain, StringComparison.Ordinal)) return false;

            for (int i = 0; i < plain.Length; i++)
            {
                var want = (styles != null && i < styles.Count) ? styles[i] : new CharStyle();
                var got = (i < backStyles.Count) ? backStyles[i] : new CharStyle();
                if (!want.Equals(got)) return false;
            }
            return true;
        }
    }
}
