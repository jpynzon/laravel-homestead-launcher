using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using try_cs.Controls;
using try_cs.Models;
using try_cs.Services;

namespace try_cs
{
    public partial class MainWindow : Window
    {
        private const double NotesColumnWidth = 380;

        private readonly LauncherService launcher = new LauncherService();
        private readonly NotesService notesService = new NotesService();
        private TerminalControl terminal;
        private HomesteadStatus currentStatus;
        private Storyboard beaconPulse;
        private bool pulsing;
        private bool ready;
        private bool busy;

        private bool notesOpen;
        private bool notesDirty;
        private bool loadingNotes;
        private string notesFile;
        private double appliedNotesDelta;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
        }

        protected override void OnClosed(EventArgs e)
        {
            try { terminal?.Shutdown(); } catch { /* ignore */ }
            base.OnClosed(e);
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            beaconPulse = (Storyboard)FindResource("BeaconPulse");
            PreloadNotes();

            string path = launcher.ResolveAndApply();
            if (path == null)
            {
                EnterSetupMode();
                return;
            }

            await UseHomestead(path, initial: true);
        }

        private async Task UseHomestead(string path, bool initial)
        {
            ready = true;
            SetupPanel.Visibility = Visibility.Collapsed;
            PathText.Text = path;
            Log(initial ? $"Homestead found at {path}" : $"Using Homestead at {path}");

            SetBusy("Checking status…");
            await RefreshStatusAsync();
            ClearBusy();
        }

        private void EnterSetupMode()
        {
            ready = false;
            SetupPanel.Visibility = Visibility.Visible;
            PathText.Text = "";
            StateMachine.Text = "—";
            StateProvider.Text = "";
            currentStatus = new HomesteadStatus { State = HomesteadState.Unknown };

            SetState("Not found", "#E8A33D",
                "Locate your Homestead folder, or clone a new one.", pulse: false);

            Log("No Homestead folder found automatically.");
            if (launcher.SearchedLocations.Count > 0)
                Log("Searched: " + string.Join("  |  ", launcher.SearchedLocations));
            Log("Use “Locate Homestead…” to pick it, or “Clone new Homestead”.");

            ApplyEnables();
        }

        // ---- Actions ---------------------------------------------------------

        private async void Up_Click(object sender, RoutedEventArgs e)
            => await Run("up", cb => launcher.UpAsync(cb));

        private async void Halt_Click(object sender, RoutedEventArgs e)
            => await Run("halt", cb => launcher.HaltAsync(cb));

        private async void Reload_Click(object sender, RoutedEventArgs e)
            => await Run("reload --provision", cb => launcher.ReloadAsync(cb));

        private async void Status_Click(object sender, RoutedEventArgs e)
            => await Run("status", cb => launcher.StatusAsync(cb));

        private async Task Run(string label, Func<Action<string>, Task<int>> action)
        {
            if (!ready || busy) return;

            SetBusy($"Running vagrant {label}…");
            Log("");
            Log("$ vagrant " + label);

            int exitCode = await action(Log);
            Log(exitCode == 0 ? "[done] " + label : $"[exit {exitCode}] {label}");

            await RefreshStatusAsync();
            ClearBusy();
        }

        // ---- SSH terminal ----------------------------------------------------

        private void Ssh_Click(object sender, RoutedEventArgs e)
        {
            if (terminal != null) Disconnect();
            else Connect();
        }

        private void Connect()
        {
            terminal = new TerminalControl();
            terminal.SessionExited += OnSessionExited;

            ConsoleHost.Children.Add(terminal);
            OutputBox.Visibility = Visibility.Collapsed;
            ConsoleTitle.Text = "Terminal · SSH";
            SshButton.Content = "Disconnect SSH";

            terminal.Launch(launcher.Environment.Directory);
            terminal.Focus();
        }

        private void Disconnect() => terminal?.Shutdown();

        private void OnSessionExited()
        {
            if (terminal != null)
            {
                terminal.SessionExited -= OnSessionExited;
                ConsoleHost.Children.Remove(terminal);
                terminal = null;
            }

            OutputBox.Visibility = Visibility.Visible;
            ConsoleTitle.Text = "Output";
            SshButton.Content = "Open SSH terminal";
            Log("--- SSH session ended. ---");
            ApplyEnables();
        }

        private void Clear_Click(object sender, RoutedEventArgs e) => OutputBox.Clear();

        // ---- Status --------------------------------------------------------

        private async Task RefreshStatusAsync()
        {
            currentStatus = await launcher.GetStatusAsync();
            ApplyStatus(currentStatus);
        }

        private void ApplyStatus(HomesteadStatus status)
        {
            StateMachine.Text = string.IsNullOrWhiteSpace(status.MachineName) ? "—" : status.MachineName;
            StateProvider.Text = string.IsNullOrWhiteSpace(status.Provider) ? "" : "provider · " + status.Provider;

            switch (status.State)
            {
                case HomesteadState.Running:
                    SetState("Running", "#46C46A", "Machine is up — SSH ready", pulse: true);
                    break;
                case HomesteadState.PoweredOff:
                    SetState("Stopped", "#E8A33D", "Machine is powered off", pulse: false);
                    break;
                case HomesteadState.Saved:
                    SetState("Suspended", "#E8A33D", "Saved state — start to resume", pulse: false);
                    break;
                case HomesteadState.Aborted:
                    SetState("Aborted", "#E8A33D", "Stopped unexpectedly — start to recover", pulse: false);
                    break;
                case HomesteadState.NotCreated:
                    SetState("Not created", "#C9BFB3", "Run start to build the machine", pulse: false);
                    break;
                default:
                    SetState("Unknown", "#FF5A4D", "Could not read machine status", pulse: false);
                    break;
            }

            ApplyEnables();
        }

        private void SetState(string text, string hex, string hint, bool pulse)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();

            StateText.Text = text;
            StateText.Foreground = brush;
            StateHint.Text = hint;
            Beacon.Fill = brush;
            BeaconHalo.Fill = brush;

            if (pulse) StartPulse();
            else StopPulse();
        }

        // ---- Setup (locate / clone) -----------------------------------------

        private async void Locate_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select your Homestead folder (the one containing the Vagrantfile).",
                ShowNewFolderButton = false
            };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            string picked = dialog.SelectedPath;
            if (launcher.TryValidate(picked))
            {
                launcher.SetDirectory(picked);
                await UseHomestead(picked, initial: false);
            }
            else
            {
                MessageBox.Show(this,
                    "No Vagrantfile was found in:\n" + picked +
                    "\n\nPick the Homestead folder that contains a Vagrantfile.",
                    "Locate Homestead", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void Clone_Click(object sender, RoutedEventArgs e)
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string target = Path.Combine(profile, "Homestead");

            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            {
                MessageBox.Show(this,
                    "A non-empty folder already exists at:\n" + target +
                    "\n\nUse “Locate Homestead…” to point to it, or remove it first.",
                    "Clone Homestead", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirm = MessageBox.Show(this,
                "Clone Laravel Homestead into:\n" + target +
                "\n\nThis downloads the official repository with Git and runs its init script. Continue?",
                "Clone Homestead", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            SetBusy("Cloning Homestead…");
            Log("");
            int code = await launcher.CloneAsync(target, Log);
            ClearBusy();

            if (code == 0 && launcher.TryValidate(target))
            {
                launcher.SetDirectory(target);
                Log("[done] Homestead ready at " + target);
                await UseHomestead(target, initial: false);
            }
            else
            {
                Log("[failed] Clone did not complete.");
            }
        }

        // ---- Enable / busy state --------------------------------------------

        private void ApplyEnables()
        {
            var state = currentStatus?.State ?? HomesteadState.Unknown;
            bool running = ready && state == HomesteadState.Running;
            bool created = ready && state != HomesteadState.NotCreated && state != HomesteadState.Unknown;
            bool interactive = ready && !busy;

            UpButton.IsEnabled = interactive && !running;
            HaltButton.IsEnabled = interactive && running;
            ReloadButton.IsEnabled = interactive && created;
            StatusButton.IsEnabled = interactive;
            SshButton.IsEnabled = terminal != null || (interactive && running);

            LocateButton.IsEnabled = !busy;
            CloneButton.IsEnabled = !busy;
        }

        private void SetBusy(string message)
        {
            busy = true;
            BusyText.Text = message;
            ApplyEnables();
        }

        private void ClearBusy()
        {
            busy = false;
            BusyText.Text = "";
            ApplyEnables();
        }

        // ---- Beacon pulse ----------------------------------------------------

        private void StartPulse()
        {
            if (pulsing) return;
            pulsing = true;
            beaconPulse.Begin(BeaconHalo, true);
        }

        private void StopPulse()
        {
            if (pulsing)
            {
                beaconPulse.Stop(BeaconHalo);
                pulsing = false;
            }
            BeaconHalo.Opacity = 0.3;
        }

        // ---- Notes -----------------------------------------------------------

        private void PreloadNotes()
        {
            string last = notesService.LoadLastPath();
            if (last == null) return;

            try
            {
                loadingNotes = true;
                NotesBox.Text = notesService.Read(last);
                notesFile = last;
                notesDirty = false;
                UpdateNotesTitle();
            }
            catch { /* ignore a missing/locked file */ }
            finally { loadingNotes = false; }
        }

        private void Notes_Click(object sender, RoutedEventArgs e)
        {
            notesOpen = !notesOpen;

            if (notesOpen)
            {
                NotesColumn.Width = new GridLength(NotesColumnWidth);
                NotesPanel.Visibility = Visibility.Visible;
                NotesButton.Foreground = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#F9463B"));

                if (WindowState == WindowState.Normal)
                {
                    double old = Width;
                    Width = Math.Min(SystemParameters.WorkArea.Width, Width + NotesColumnWidth);
                    appliedNotesDelta = Width - old;
                }
                NotesBox.Focus();
            }
            else
            {
                NotesColumn.Width = new GridLength(0);
                NotesPanel.Visibility = Visibility.Collapsed;
                NotesButton.Foreground = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#A79A8D"));

                if (WindowState == WindowState.Normal && appliedNotesDelta > 0)
                {
                    Width = Math.Max(MinWidth, Width - appliedNotesDelta);
                    appliedNotesDelta = 0;
                }
            }
        }

        private void NotesBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (loadingNotes) return;
            notesDirty = true;
            UpdateNotesTitle();
        }

        private void NotesBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                DoSave();
            }
        }

        private void NotesNew_Click(object sender, RoutedEventArgs e)
        {
            if (!ConfirmDiscard()) return;
            loadingNotes = true;
            NotesBox.Clear();
            loadingNotes = false;
            notesFile = null;
            notesDirty = false;
            UpdateNotesTitle();
            NotesBox.Focus();
        }

        private void NotesOpen_Click(object sender, RoutedEventArgs e)
        {
            if (!ConfirmDiscard()) return;

            var dialog = new OpenFileDialog
            {
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                DefaultExt = ".txt"
            };
            if (notesFile != null) dialog.InitialDirectory = Path.GetDirectoryName(notesFile);

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    loadingNotes = true;
                    NotesBox.Text = notesService.Read(dialog.FileName);
                    notesFile = dialog.FileName;
                    notesDirty = false;
                    UpdateNotesTitle();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Couldn't open the file:\n" + ex.Message,
                        "Notes", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                finally { loadingNotes = false; }
            }
        }

        private void NotesSave_Click(object sender, RoutedEventArgs e) => DoSave();

        private void NotesSaveAs_Click(object sender, RoutedEventArgs e) => DoSaveAs();

        private bool DoSave()
        {
            if (notesFile == null) return DoSaveAs();

            try
            {
                notesService.Save(notesFile, NotesBox.Text);
                notesDirty = false;
                UpdateNotesTitle();
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't save the file:\n" + ex.Message,
                    "Notes", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        private bool DoSaveAs()
        {
            var dialog = new SaveFileDialog
            {
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                DefaultExt = ".txt",
                FileName = notesFile != null ? Path.GetFileName(notesFile) : "notes.txt"
            };
            if (notesFile != null) dialog.InitialDirectory = Path.GetDirectoryName(notesFile);

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    notesService.Save(dialog.FileName, NotesBox.Text);
                    notesFile = dialog.FileName;
                    notesDirty = false;
                    UpdateNotesTitle();
                    return true;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Couldn't save the file:\n" + ex.Message,
                        "Notes", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            return false;
        }

        private bool ConfirmDiscard()
        {
            if (!notesDirty) return true;
            var result = MessageBox.Show(this,
                "You have unsaved notes. Discard them?", "Notes",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            return result == MessageBoxResult.Yes;
        }

        private void UpdateNotesTitle()
        {
            string name = notesFile != null ? Path.GetFileName(notesFile) : "Untitled";
            NotesFile.Text = (notesDirty ? "• " : "") + name;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (notesDirty)
            {
                var result = MessageBox.Show(this,
                    "Save changes to your notes before closing?", "Notes",
                    MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

                if (result == MessageBoxResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }
                if (result == MessageBoxResult.Yes && !DoSave())
                {
                    e.Cancel = true;   // save failed or was cancelled
                    return;
                }
            }
            base.OnClosing(e);
        }

        // ---- Log -------------------------------------------------------------

        private void Log(string line)
        {
            Dispatcher.Invoke(() =>
            {
                OutputBox.AppendText(line + Environment.NewLine);
                OutputBox.ScrollToEnd();
            });
        }
    }
}
