using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using try_cs.Services;

namespace try_cs.Controls
{
    /// <summary>
    /// A self-rendering terminal control. It owns a <see cref="PtySession"/>,
    /// feeds its output into a <see cref="VtScreen"/> emulator, draws the screen
    /// grid in <see cref="OnRender"/>, and forwards keystrokes back to the pty.
    /// </summary>
    public class TerminalControl : Control
    {
        private const double FontEmSize = 14.0;

        private readonly Typeface typeface;
        private readonly Typeface typefaceBold;
        private readonly Dictionary<int, SolidColorBrush> brushCache = new Dictionary<int, SolidColorBrush>();
        private readonly SolidColorBrush defaultBgBrush;
        private readonly SolidColorBrush cursorBrush;

        private double cellWidth = 8;
        private double cellHeight = 16;
        private double pixelsPerDip = 1.0;

        private VtScreen screen;
        private PtySession session;
        private string pendingWorkingDir;
        private bool started;

        /// <summary>Raised on the UI thread when the underlying session ends.</summary>
        public event Action SessionExited;

        public TerminalControl()
        {
            Focusable = true;
            FocusVisualStyle = null;
            ClipToBounds = true;
            SnapsToDevicePixels = true;

            typeface = new Typeface(new FontFamily("Consolas"),
                FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            typefaceBold = new Typeface(new FontFamily("Consolas"),
                FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

            defaultBgBrush = Frozen(0x0A0E17);
            cursorBrush = Frozen(0x22C55E);

            ComputeMetrics();

            Loaded += (s, e) => { UpdateDpi(); RecomputeGrid(); Focus(); };
            SizeChanged += (s, e) => RecomputeGrid();
        }

        /// <summary>Requests a session; it starts once the grid size is known.</summary>
        public void Launch(string workingDirectory)
        {
            pendingWorkingDir = workingDirectory;
            if (screen != null) EnsureStarted();
        }

        public void Shutdown()
        {
            try { session?.Stop(); } catch { /* ignore */ }
        }

        // ---- Sizing ----------------------------------------------------------

        private void ComputeMetrics()
        {
            if (typeface.TryGetGlyphTypeface(out GlyphTypeface gt) &&
                gt.CharacterToGlyphMap.TryGetValue('M', out ushort glyph))
            {
                cellWidth = gt.AdvanceWidths[glyph] * FontEmSize;
                cellHeight = gt.Height * FontEmSize;
            }
        }

        private void UpdateDpi()
        {
            try { pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip; }
            catch { pixelsPerDip = 1.0; }
        }

        private void RecomputeGrid()
        {
            if (ActualWidth <= 0 || ActualHeight <= 0) return;

            int cols = Math.Max(1, (int)(ActualWidth / cellWidth));
            int rows = Math.Max(1, (int)(ActualHeight / cellHeight));

            if (screen == null)
            {
                screen = new VtScreen(cols, rows);
            }
            else if (cols != screen.Cols || rows != screen.Rows)
            {
                screen.Resize(cols, rows);
                session?.Resize(cols, rows);
            }

            EnsureStarted();
            InvalidateVisual();
        }

        private void EnsureStarted()
        {
            if (started || screen == null || string.IsNullOrEmpty(pendingWorkingDir)) return;

            started = true;
            session = new PtySession();
            session.DataReceived += OnData;
            session.Exited += OnExited;

            try
            {
                session.Start(pendingWorkingDir, "cmd.exe /c vagrant ssh", screen.Cols, screen.Rows);
            }
            catch (Exception ex)
            {
                started = false;
                screen.Feed("\r\n  Failed to start terminal:\r\n  " + ex.Message + "\r\n");
                InvalidateVisual();
            }
        }

        // ---- Session events (background thread -> UI) ------------------------

        private void OnData(char[] data, int count)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (screen == null) return;
                screen.Feed(data, count);
                InvalidateVisual();
            }));
        }

        private void OnExited()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                SessionExited?.Invoke();
            }));
        }

        // ---- Keyboard --------------------------------------------------------

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            Focus();
            base.OnMouseDown(e);
        }

        protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnGotKeyboardFocus(e);
            InvalidateVisual();
        }

        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnLostKeyboardFocus(e);
            InvalidateVisual();
        }

        protected override void OnTextInput(TextCompositionEventArgs e)
        {
            if (session != null && session.IsRunning && !string.IsNullOrEmpty(e.Text))
            {
                // Printable characters only; control keys are handled in OnPreviewKeyDown.
                var sb = new StringBuilder(e.Text.Length);
                foreach (char c in e.Text)
                    if (c >= ' ') sb.Append(c);

                if (sb.Length > 0)
                {
                    session.Write(sb.ToString());
                    e.Handled = true;
                }
            }
            base.OnTextInput(e);
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (session == null || !session.IsRunning)
            {
                base.OnPreviewKeyDown(e);
                return;
            }

            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            string send = null;

            if (ctrl && e.Key >= Key.A && e.Key <= Key.Z)
            {
                send = ((char)(e.Key - Key.A + 1)).ToString();  // Ctrl+A..Z -> 0x01..0x1A
            }
            else
            {
                switch (e.Key)
                {
                    case Key.Enter: send = "\r"; break;
                    case Key.Back: send = "\x7f"; break;
                    case Key.Tab: send = "\t"; break;
                    case Key.Escape: send = "\x1b"; break;
                    case Key.Up: send = "\x1b[A"; break;
                    case Key.Down: send = "\x1b[B"; break;
                    case Key.Right: send = "\x1b[C"; break;
                    case Key.Left: send = "\x1b[D"; break;
                    case Key.Home: send = "\x1b[H"; break;
                    case Key.End: send = "\x1b[F"; break;
                    case Key.Delete: send = "\x1b[3~"; break;
                    case Key.Insert: send = "\x1b[2~"; break;
                    case Key.PageUp: send = "\x1b[5~"; break;
                    case Key.PageDown: send = "\x1b[6~"; break;
                }
            }

            if (send != null)
            {
                session.Write(send);
                e.Handled = true;
                return;
            }

            base.OnPreviewKeyDown(e);
        }

        // ---- Rendering -------------------------------------------------------

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(defaultBgBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
            if (screen == null) return;

            var sb = new StringBuilder();
            for (int r = 0; r < screen.Rows; r++)
            {
                double y = r * cellHeight;
                int c = 0;
                while (c < screen.Cols)
                {
                    TerminalCell first = screen.CellAt(r, c);
                    int fg = first.Inverse ? first.Bg : first.Fg;
                    int bg = first.Inverse ? first.Fg : first.Bg;
                    bool bold = first.Bold;

                    int start = c;
                    sb.Clear();
                    while (c < screen.Cols)
                    {
                        TerminalCell cell = screen.CellAt(r, c);
                        int cf = cell.Inverse ? cell.Bg : cell.Fg;
                        int cb = cell.Inverse ? cell.Fg : cell.Bg;
                        if (cf != fg || cb != bg || cell.Bold != bold) break;
                        sb.Append(cell.Ch == '\0' ? ' ' : cell.Ch);
                        c++;
                    }

                    double x = start * cellWidth;
                    double w = (c - start) * cellWidth;

                    if (bg != screen.DefaultBg)
                        dc.DrawRectangle(BrushFor(bg), null, new Rect(x, y, w, cellHeight));

                    string text = sb.ToString();
                    if (text.Trim().Length > 0)
                    {
                        var ft = new FormattedText(text, CultureInfo.CurrentCulture,
                            FlowDirection.LeftToRight, bold ? typefaceBold : typeface,
                            FontEmSize, BrushFor(fg), pixelsPerDip);
                        dc.DrawText(ft, new Point(x, y));
                    }
                }
            }

            DrawCursor(dc);
        }

        private void DrawCursor(DrawingContext dc)
        {
            if (!screen.CursorVisible) return;
            if (screen.CursorX >= screen.Cols || screen.CursorY >= screen.Rows) return;

            double x = screen.CursorX * cellWidth;
            double y = screen.CursorY * cellHeight;
            var rect = new Rect(x, y, cellWidth, cellHeight);

            if (IsKeyboardFocused)
            {
                dc.DrawRectangle(cursorBrush, null, rect);
                TerminalCell cell = screen.CellAt(screen.CursorY, screen.CursorX);
                char ch = cell.Ch == '\0' ? ' ' : cell.Ch;
                if (ch != ' ')
                {
                    var ft = new FormattedText(ch.ToString(), CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight, typeface, FontEmSize, defaultBgBrush, pixelsPerDip);
                    dc.DrawText(ft, new Point(x, y));
                }
            }
            else
            {
                dc.DrawRectangle(null, new Pen(cursorBrush, 1), rect);
            }
        }

        private SolidColorBrush BrushFor(int rgb)
        {
            if (!brushCache.TryGetValue(rgb, out SolidColorBrush brush))
            {
                brush = Frozen(rgb);
                brushCache[rgb] = brush;
            }
            return brush;
        }

        private static SolidColorBrush Frozen(int rgb)
        {
            var brush = new SolidColorBrush(Color.FromRgb(
                (byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF)));
            brush.Freeze();
            return brush;
        }
    }
}
