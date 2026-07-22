using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace try_cs
{
    /// <summary>
    /// A small dark-themed text editor for a single config file. Saving tries a
    /// normal write first; if the OS denies it (e.g. the hosts file), it offers
    /// to save again with administrator elevation via a UAC prompt.
    /// </summary>
    public partial class ConfigEditorWindow : Window
    {
        private readonly string filePath;
        private readonly bool requiresAdmin;
        private bool dirty;
        private bool loading;

        public ConfigEditorWindow(Window owner, string title, string path, bool requiresAdmin)
        {
            InitializeComponent();

            filePath = path;
            this.requiresAdmin = requiresAdmin;

            Owner = owner;
            if (owner != null) Icon = owner.Icon;

            Title = title;
            TitleText.Text = title;
            PathText.Text = path;
            AdminNote.Visibility = requiresAdmin ? Visibility.Visible : Visibility.Collapsed;

            LoadFile();
        }

        private void LoadFile()
        {
            try
            {
                loading = true;
                if (File.Exists(filePath))
                {
                    Editor.Text = File.ReadAllText(filePath);
                    SetStatus("Loaded.");
                }
                else
                {
                    Editor.Text = "";
                    SetStatus("File does not exist yet — it will be created on save.");
                }
                dirty = false;
            }
            catch (Exception ex)
            {
                SetStatus("Couldn't read the file: " + ex.Message);
            }
            finally
            {
                loading = false;
            }
        }

        private void Editor_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!loading) dirty = true;
        }

        private void Reload_Click(object sender, RoutedEventArgs e)
        {
            if (dirty)
            {
                var r = MessageBox.Show(this, "Discard your unsaved changes and reload from disk?",
                    Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) return;
            }
            LoadFile();
        }

        private void Save_Click(object sender, RoutedEventArgs e) => Save();

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void Save()
        {
            try
            {
                File.WriteAllText(filePath, Editor.Text);
                dirty = false;
                SetStatus("Saved.");
                ShowToast("Saved");
            }
            catch (UnauthorizedAccessException)
            {
                if (ElevatedSave())
                {
                    dirty = false;
                    SetStatus("Saved as administrator.");
                    ShowToast("Saved as administrator");
                }
            }
            catch (IOException ex)
            {
                MessageBox.Show(this, "Couldn't save the file:\n" + ex.Message,
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// Writes the content to a temp file, then copies it over the target
        /// using an elevated process (triggers a UAC prompt).
        /// </summary>
        private bool ElevatedSave()
        {
            var confirm = MessageBox.Show(this,
                "Saving this file requires administrator rights.\n\nApprove the elevation prompt to save?",
                Title, MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (confirm != MessageBoxResult.OK) { SetStatus("Save cancelled."); return false; }

            string temp = Path.Combine(Path.GetTempPath(), "cfg_" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temp, Editor.Text);

                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c copy /y \"{temp}\" \"{filePath}\"",
                    UseShellExecute = true,
                    Verb = "runas",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using (var process = Process.Start(startInfo))
                {
                    process.WaitForExit();
                    if (process.ExitCode == 0) return true;

                    MessageBox.Show(this, "The elevated save did not complete (exit code "
                        + process.ExitCode + ").", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }
            catch (Win32Exception)
            {
                // User dismissed the UAC prompt.
                SetStatus("Save cancelled — administrator approval was not granted.");
                return false;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't save the file:\n" + ex.Message,
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* ignore */ }
            }
        }

        private void SetStatus(string text) => StatusText.Text = text;

        private void ShowToast(string message)
        {
            ToastText.Text = message;

            var fade = new DoubleAnimationUsingKeyFrames();
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180))));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1700))));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2200))));

            Storyboard.SetTarget(fade, Toast);
            Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));

            var storyboard = new Storyboard();
            storyboard.Children.Add(fade);
            storyboard.Begin();
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                Save();
            }
            base.OnPreviewKeyDown(e);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (dirty)
            {
                var r = MessageBox.Show(this, "You have unsaved changes. Save before closing?",
                    Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
                if (r == MessageBoxResult.Yes)
                {
                    Save();
                    if (dirty) { e.Cancel = true; return; }  // save failed/cancelled
                }
            }
            base.OnClosing(e);
        }
    }
}
