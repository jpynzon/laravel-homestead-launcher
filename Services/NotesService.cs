using System;
using System.IO;

namespace HomesteadLauncher.Services
{
    /// <summary>
    /// Reads and writes the notepad's .txt files and remembers the last file
    /// used, so it can be reloaded automatically on the next launch.
    /// </summary>
    public class NotesService
    {
        private readonly string stateFile;

        public NotesService()
        {
            stateFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "notes.last");
        }

        /// <summary>Returns the last-used file path if it still exists, else null.</summary>
        public string LoadLastPath()
        {
            try
            {
                if (File.Exists(stateFile))
                {
                    string path = File.ReadAllText(stateFile).Trim();
                    if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
                }
            }
            catch { /* ignore */ }
            return null;
        }

        public string Read(string path)
        {
            string text = File.ReadAllText(path);
            RememberPath(path);
            return text;
        }

        public void Save(string path, string text)
        {
            File.WriteAllText(path, text);
            RememberPath(path);
        }

        private void RememberPath(string path)
        {
            try { File.WriteAllText(stateFile, path); } catch { /* ignore */ }
        }
    }
}
