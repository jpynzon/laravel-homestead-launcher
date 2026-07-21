using System;
using System.Collections.Generic;
using System.Text;

namespace try_cs.Controls
{
    /// <summary>One character cell: glyph plus colour/attribute state.</summary>
    public struct TerminalCell
    {
        public char Ch;
        public int Fg;   // 0xRRGGBB
        public int Bg;   // 0xRRGGBB
        public bool Bold;
        public bool Inverse;
    }

    /// <summary>
    /// A minimal terminal emulator: an in-memory screen grid plus a state
    /// machine that interprets the common subset of VT100/xterm escape
    /// sequences (cursor movement, SGR colours, erase, scroll regions, insert/
    /// delete, and the alternate screen buffer used by full-screen programs).
    /// It is not a complete emulator, but handles everyday shell usage.
    /// </summary>
    public class VtScreen
    {
        public int Cols { get; private set; }
        public int Rows { get; private set; }
        public int CursorX { get; private set; }
        public int CursorY { get; private set; }
        public bool CursorVisible { get; private set; } = true;

        public int DefaultFg = 0xC9D4E7;
        public int DefaultBg = 0x0A0E17;

        private TerminalCell[] main;
        private TerminalCell[] alt;
        private TerminalCell[] cur;
        private bool usingAlt;

        private int curFg, curBg;
        private bool curBold, curInverse;
        private int savedX, savedY;
        private int scrollTop, scrollBottom;

        private enum State { Normal, Esc, Csi, Osc, OscEsc, EscConsume }
        private State state = State.Normal;
        private readonly StringBuilder csi = new StringBuilder();

        public VtScreen(int cols, int rows)
        {
            Cols = Math.Max(1, cols);
            Rows = Math.Max(1, rows);
            curFg = DefaultFg;
            curBg = DefaultBg;
            main = NewBuffer();
            alt = NewBuffer();
            cur = main;
            scrollTop = 0;
            scrollBottom = Rows - 1;
        }

        private TerminalCell[] NewBuffer()
        {
            var buf = new TerminalCell[Cols * Rows];
            for (int i = 0; i < buf.Length; i++) buf[i] = Blank();
            return buf;
        }

        private TerminalCell Blank()
        {
            return new TerminalCell { Ch = ' ', Fg = curFg, Bg = curBg, Bold = false, Inverse = false };
        }

        public TerminalCell CellAt(int row, int col)
        {
            return cur[row * Cols + col];
        }

        public void Resize(int cols, int rows)
        {
            cols = Math.Max(1, cols);
            rows = Math.Max(1, rows);
            if (cols == Cols && rows == Rows) return;

            var newMain = ResizeBuffer(main, cols, rows);
            var newAlt = new TerminalCell[cols * rows];
            for (int i = 0; i < newAlt.Length; i++) newAlt[i] = Blank();

            main = newMain;
            alt = newAlt;
            Cols = cols;
            Rows = rows;
            cur = usingAlt ? alt : main;

            scrollTop = 0;
            scrollBottom = Rows - 1;
            CursorX = Math.Min(CursorX, Cols - 1);
            CursorY = Math.Min(CursorY, Rows - 1);
        }

        private TerminalCell[] ResizeBuffer(TerminalCell[] src, int cols, int rows)
        {
            var dst = new TerminalCell[cols * rows];
            for (int i = 0; i < dst.Length; i++) dst[i] = Blank();
            int copyRows = Math.Min(rows, Rows);
            int copyCols = Math.Min(cols, Cols);
            for (int r = 0; r < copyRows; r++)
                for (int c = 0; c < copyCols; c++)
                    dst[r * cols + c] = src[r * Cols + c];
            return dst;
        }

        // ---- Feed / parser ---------------------------------------------------

        public void Feed(char[] data, int count)
        {
            for (int i = 0; i < count; i++) Feed(data[i]);
        }

        public void Feed(string text)
        {
            foreach (char c in text) Feed(c);
        }

        private void Feed(char c)
        {
            switch (state)
            {
                case State.Normal: FeedNormal(c); break;
                case State.Esc: FeedEsc(c); break;
                case State.Csi: FeedCsi(c); break;
                case State.Osc:
                    if (c == '\a') state = State.Normal;
                    else if (c == '\x1b') state = State.OscEsc;
                    break;
                case State.OscEsc:
                    // Swallow the terminating backslash of ST (ESC \).
                    state = State.Normal;
                    break;
                case State.EscConsume:
                    state = State.Normal;
                    break;
            }
        }

        private void FeedNormal(char c)
        {
            switch (c)
            {
                case '\x1b': state = State.Esc; break;
                case '\r': CursorX = 0; break;
                case '\n': LineFeed(); break;
                case '\b': if (CursorX > 0) CursorX--; break;
                case '\t': CursorX = Math.Min(Cols - 1, ((CursorX / 8) + 1) * 8); break;
                case '\a': break;
                default:
                    if (c >= ' ') Put(c);
                    break;
            }
        }

        private void FeedEsc(char c)
        {
            switch (c)
            {
                case '[': csi.Clear(); state = State.Csi; break;
                case ']': state = State.Osc; break;
                case '(':
                case ')':
                case '*':
                case '+': state = State.EscConsume; break;   // charset designators
                case '=':
                case '>': state = State.Normal; break;         // keypad modes
                case '7': savedX = CursorX; savedY = CursorY; state = State.Normal; break;
                case '8': CursorX = savedX; CursorY = savedY; state = State.Normal; break;
                case 'M': ReverseIndex(); state = State.Normal; break;
                case 'c': FullReset(); state = State.Normal; break;
                default: state = State.Normal; break;
            }
        }

        private void FeedCsi(char c)
        {
            if (c >= '\x40' && c <= '\x7e')
            {
                DispatchCsi(csi.ToString(), c);
                state = State.Normal;
            }
            else
            {
                csi.Append(c);
            }
        }

        private void DispatchCsi(string buffer, char final)
        {
            bool priv = buffer.StartsWith("?");
            string body = priv ? buffer.Substring(1) : buffer;
            int[] ps = ParseParams(body);
            int p0 = ps.Length > 0 ? ps[0] : 0;
            int n = Math.Max(1, p0);

            switch (final)
            {
                case 'H':
                case 'f':
                    {
                        int row = (ps.Length > 0 && ps[0] > 0 ? ps[0] : 1) - 1;
                        int col = (ps.Length > 1 && ps[1] > 0 ? ps[1] : 1) - 1;
                        CursorY = Clamp(row, 0, Rows - 1);
                        CursorX = Clamp(col, 0, Cols - 1);
                        break;
                    }
                case 'A': CursorY = Math.Max(0, CursorY - n); break;
                case 'B': CursorY = Math.Min(Rows - 1, CursorY + n); break;
                case 'C': CursorX = Math.Min(Cols - 1, CursorX + n); break;
                case 'D': CursorX = Math.Max(0, CursorX - n); break;
                case 'E': CursorY = Math.Min(Rows - 1, CursorY + n); CursorX = 0; break;
                case 'F': CursorY = Math.Max(0, CursorY - n); CursorX = 0; break;
                case 'G':
                case '`': CursorX = Clamp((ps.Length > 0 && ps[0] > 0 ? ps[0] : 1) - 1, 0, Cols - 1); break;
                case 'd': CursorY = Clamp((ps.Length > 0 && ps[0] > 0 ? ps[0] : 1) - 1, 0, Rows - 1); break;
                case 'J': EraseDisplay(p0); break;
                case 'K': EraseLine(p0); break;
                case 'm': ApplySgr(ps); break;
                case 'P': DeleteChars(n); break;
                case '@': InsertChars(n); break;
                case 'L': InsertLines(n); break;
                case 'M': DeleteLines(n); break;
                case 'S': ScrollUp(n); break;
                case 'T': ScrollDown(n); break;
                case 'X': EraseChars(n); break;
                case 'r':
                    scrollTop = (ps.Length > 0 && ps[0] > 0 ? ps[0] : 1) - 1;
                    scrollBottom = (ps.Length > 1 && ps[1] > 0 ? ps[1] : Rows) - 1;
                    scrollTop = Clamp(scrollTop, 0, Rows - 1);
                    scrollBottom = Clamp(scrollBottom, 0, Rows - 1);
                    CursorX = 0; CursorY = scrollTop;
                    break;
                case 'h': if (priv) SetPrivateMode(ps, true); break;
                case 'l': if (priv) SetPrivateMode(ps, false); break;
                case 's': savedX = CursorX; savedY = CursorY; break;
                case 'u': CursorX = savedX; CursorY = savedY; break;
            }
        }

        private static int[] ParseParams(string body)
        {
            if (string.IsNullOrEmpty(body)) return new int[0];
            string[] parts = body.Split(';');
            var result = new List<int>(parts.Length);
            foreach (string part in parts)
                result.Add(int.TryParse(part, out int v) ? v : 0);
            return result.ToArray();
        }

        // ---- Writing / scrolling --------------------------------------------

        private void Put(char c)
        {
            if (CursorX >= Cols)
            {
                CursorX = 0;
                LineFeed();
            }
            cur[CursorY * Cols + CursorX] = new TerminalCell
            {
                Ch = c,
                Fg = curFg,
                Bg = curBg,
                Bold = curBold,
                Inverse = curInverse
            };
            CursorX++;
        }

        private void LineFeed()
        {
            if (CursorY >= scrollBottom) ScrollUp(1);
            else CursorY++;
        }

        private void ReverseIndex()
        {
            if (CursorY <= scrollTop) ScrollDown(1);
            else CursorY--;
        }

        private void ScrollUp(int lines)
        {
            for (int k = 0; k < lines; k++)
            {
                for (int r = scrollTop; r < scrollBottom; r++)
                    Array.Copy(cur, (r + 1) * Cols, cur, r * Cols, Cols);
                ClearLine(scrollBottom);
            }
        }

        private void ScrollDown(int lines)
        {
            for (int k = 0; k < lines; k++)
            {
                for (int r = scrollBottom; r > scrollTop; r--)
                    Array.Copy(cur, (r - 1) * Cols, cur, r * Cols, Cols);
                ClearLine(scrollTop);
            }
        }

        private void ClearLine(int row)
        {
            for (int c = 0; c < Cols; c++) cur[row * Cols + c] = Blank();
        }

        // ---- Erase / insert / delete ----------------------------------------

        private void EraseDisplay(int mode)
        {
            if (mode == 2 || mode == 3)
            {
                for (int i = 0; i < cur.Length; i++) cur[i] = Blank();
            }
            else if (mode == 1)
            {
                for (int r = 0; r < CursorY; r++) ClearLine(r);
                for (int c = 0; c <= CursorX && c < Cols; c++) cur[CursorY * Cols + c] = Blank();
            }
            else // 0
            {
                for (int c = CursorX; c < Cols; c++) cur[CursorY * Cols + c] = Blank();
                for (int r = CursorY + 1; r < Rows; r++) ClearLine(r);
            }
        }

        private void EraseLine(int mode)
        {
            if (mode == 1)
                for (int c = 0; c <= CursorX && c < Cols; c++) cur[CursorY * Cols + c] = Blank();
            else if (mode == 2)
                ClearLine(CursorY);
            else
                for (int c = CursorX; c < Cols; c++) cur[CursorY * Cols + c] = Blank();
        }

        private void EraseChars(int count)
        {
            for (int c = CursorX; c < CursorX + count && c < Cols; c++)
                cur[CursorY * Cols + c] = Blank();
        }

        private void InsertChars(int count)
        {
            int row = CursorY;
            for (int c = Cols - 1; c >= CursorX + count; c--)
                cur[row * Cols + c] = cur[row * Cols + c - count];
            for (int c = CursorX; c < CursorX + count && c < Cols; c++)
                cur[row * Cols + c] = Blank();
        }

        private void DeleteChars(int count)
        {
            int row = CursorY;
            for (int c = CursorX; c < Cols; c++)
                cur[row * Cols + c] = (c + count < Cols) ? cur[row * Cols + c + count] : Blank();
        }

        private void InsertLines(int count)
        {
            if (CursorY < scrollTop || CursorY > scrollBottom) return;
            for (int k = 0; k < count; k++)
            {
                for (int r = scrollBottom; r > CursorY; r--)
                    Array.Copy(cur, (r - 1) * Cols, cur, r * Cols, Cols);
                ClearLine(CursorY);
            }
        }

        private void DeleteLines(int count)
        {
            if (CursorY < scrollTop || CursorY > scrollBottom) return;
            for (int k = 0; k < count; k++)
            {
                for (int r = CursorY; r < scrollBottom; r++)
                    Array.Copy(cur, (r + 1) * Cols, cur, r * Cols, Cols);
                ClearLine(scrollBottom);
            }
        }

        // ---- Modes / reset ---------------------------------------------------

        private void SetPrivateMode(int[] ps, bool set)
        {
            foreach (int p in ps)
            {
                switch (p)
                {
                    case 25: CursorVisible = set; break;
                    case 47:
                    case 1047:
                    case 1049:
                        if (set) EnterAlt(); else ExitAlt();
                        break;
                }
            }
        }

        private void EnterAlt()
        {
            if (usingAlt) return;
            savedX = CursorX; savedY = CursorY;
            usingAlt = true;
            cur = alt;
            for (int i = 0; i < cur.Length; i++) cur[i] = Blank();
            CursorX = 0; CursorY = 0;
        }

        private void ExitAlt()
        {
            if (!usingAlt) return;
            usingAlt = false;
            cur = main;
            CursorX = savedX; CursorY = savedY;
        }

        private void FullReset()
        {
            curFg = DefaultFg; curBg = DefaultBg; curBold = false; curInverse = false;
            usingAlt = false;
            cur = main;
            for (int i = 0; i < main.Length; i++) main[i] = Blank();
            CursorX = 0; CursorY = 0;
            scrollTop = 0; scrollBottom = Rows - 1;
            CursorVisible = true;
        }

        // ---- Colours (SGR) ---------------------------------------------------

        private void ApplySgr(int[] ps)
        {
            if (ps.Length == 0) ps = new[] { 0 };

            for (int i = 0; i < ps.Length; i++)
            {
                int p = ps[i];
                if (p == 0) { curFg = DefaultFg; curBg = DefaultBg; curBold = false; curInverse = false; }
                else if (p == 1) curBold = true;
                else if (p == 22) curBold = false;
                else if (p == 7) curInverse = true;
                else if (p == 27) curInverse = false;
                else if (p >= 30 && p <= 37) curFg = Ansi16(p - 30);
                else if (p >= 40 && p <= 47) curBg = Ansi16(p - 40);
                else if (p >= 90 && p <= 97) curFg = Ansi16(8 + p - 90);
                else if (p >= 100 && p <= 107) curBg = Ansi16(8 + p - 100);
                else if (p == 39) curFg = DefaultFg;
                else if (p == 49) curBg = DefaultBg;
                else if (p == 38 && i + 1 < ps.Length)
                {
                    if (ps[i + 1] == 5 && i + 2 < ps.Length) { curFg = Ansi256(ps[i + 2]); i += 2; }
                    else if (ps[i + 1] == 2 && i + 4 < ps.Length) { curFg = Rgb(ps[i + 2], ps[i + 3], ps[i + 4]); i += 4; }
                }
                else if (p == 48 && i + 1 < ps.Length)
                {
                    if (ps[i + 1] == 5 && i + 2 < ps.Length) { curBg = Ansi256(ps[i + 2]); i += 2; }
                    else if (ps[i + 1] == 2 && i + 4 < ps.Length) { curBg = Rgb(ps[i + 2], ps[i + 3], ps[i + 4]); i += 4; }
                }
            }
        }

        private static int Rgb(int r, int g, int b)
        {
            return ((r & 0xFF) << 16) | ((g & 0xFF) << 8) | (b & 0xFF);
        }

        private static readonly int[] Palette16 =
        {
            0x000000, 0xCC0000, 0x4E9A06, 0xC4A000, 0x3465A4, 0x75507B, 0x06989A, 0xD3D7CF,
            0x555753, 0xEF2929, 0x8AE234, 0xFCE94F, 0x729FCF, 0xAD7FA8, 0x34E2E2, 0xEEEEEC
        };

        private static int Ansi16(int index)
        {
            if (index < 0 || index >= Palette16.Length) return 0xC9D4E7;
            return Palette16[index];
        }

        private static int Ansi256(int index)
        {
            if (index < 16) return Ansi16(index);
            if (index < 232)
            {
                int n = index - 16;
                int r = n / 36;
                int g = (n / 6) % 6;
                int b = n % 6;
                return Rgb(Cube(r), Cube(g), Cube(b));
            }
            int gray = 8 + 10 * (index - 232);
            return Rgb(gray, gray, gray);
        }

        private static int Cube(int v)
        {
            return v == 0 ? 0 : 55 + 40 * v;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
