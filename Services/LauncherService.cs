using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using try_cs.Models;

namespace try_cs.Services
{
    /// <summary>
    /// Launches and controls a Laravel Homestead box by running vagrant
    /// commands inside the configured Homestead directory. Output is streamed
    /// back through a callback so the UI can show it live.
    /// </summary>
    public class LauncherService
    {
        private readonly string overrideFile;

        public HomesteadEnvironment Environment { get; }

        /// <summary>Paths inspected during the last <see cref="ResolveAndApply"/>.</summary>
        public List<string> SearchedLocations { get; } = new List<string>();

        public LauncherService()
        {
            overrideFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "homestead.path");
            Environment = new HomesteadEnvironment();
        }

        /// <summary>True if the folder exists and holds a Vagrantfile.</summary>
        public bool TryValidate(string path)
        {
            return !string.IsNullOrWhiteSpace(path)
                && Directory.Exists(path)
                && File.Exists(Path.Combine(path, "Vagrantfile"));
        }

        /// <summary>
        /// Finds a Homestead folder (remembered path, config, then common
        /// locations), applies the first valid one, and returns it — or null
        /// if none was found.
        /// </summary>
        public string ResolveAndApply()
        {
            SearchedLocations.Clear();
            foreach (string candidate in CandidatePaths())
            {
                if (SearchedLocations.Contains(candidate)) continue;
                SearchedLocations.Add(candidate);
                if (TryValidate(candidate))
                {
                    SetDirectory(candidate);
                    return candidate;
                }
            }
            return null;
        }

        private IEnumerable<string> CandidatePaths()
        {
            string saved = ReadOverride();
            if (!string.IsNullOrWhiteSpace(saved)) yield return saved;

            string configured = ConfigurationManager.AppSettings["HomesteadPath"];
            if (!string.IsNullOrWhiteSpace(configured)) yield return configured;

            string profile = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
            string sysDrive = Path.GetPathRoot(profile) ?? @"C:\";

            yield return Path.Combine(profile, "Homestead");
            yield return Path.Combine(profile, "~", "Homestead");
            yield return Path.Combine(sysDrive, "Homestead");
            yield return Path.Combine(profile, "code", "Homestead");
            yield return Path.Combine(profile, "Documents", "Homestead");

            // Shallow scan: any top-level folder in the profile named like Homestead.
            List<string> scanned = new List<string>();
            try
            {
                foreach (string dir in Directory.EnumerateDirectories(profile))
                {
                    string name = Path.GetFileName(dir);
                    if (name.IndexOf("homestead", StringComparison.OrdinalIgnoreCase) >= 0)
                        scanned.Add(dir);
                }
            }
            catch { /* profile not enumerable — ignore */ }

            foreach (string dir in scanned) yield return dir;
        }

        /// <summary>Sets the active folder and remembers it for next launch.</summary>
        public void SetDirectory(string path)
        {
            Environment.Directory = path;
            WriteOverride(path);
        }

        private string ReadOverride()
        {
            try
            {
                if (File.Exists(overrideFile))
                {
                    string path = File.ReadAllText(overrideFile).Trim();
                    if (path.Length > 0) return path;
                }
            }
            catch { /* ignore */ }
            return null;
        }

        private void WriteOverride(string path)
        {
            try { File.WriteAllText(overrideFile, path); } catch { /* ignore */ }
        }

        /// <summary>
        /// Clones the official Laravel Homestead repo into <paramref name="targetDir"/>
        /// and runs its Windows init script. Returns the last exit code.
        /// </summary>
        public async Task<int> CloneAsync(string targetDir, Action<string> onOutput)
        {
            string parent = Path.GetDirectoryName(targetDir.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(parent))
                parent = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);

            onOutput("Cloning Laravel Homestead into " + targetDir + " …");
            int code = await RunProcessAsync("git",
                $"clone --branch release https://github.com/laravel/homestead.git \"{targetDir}\"",
                parent, onOutput);

            if (code != 0)
            {
                onOutput("git clone failed. Make sure Git is installed and on your PATH.");
                return code;
            }

            string initBat = Path.Combine(targetDir, "init.bat");
            if (File.Exists(initBat))
            {
                onOutput("Running init.bat …");
                code = await RunProcessAsync("cmd.exe", "/c init.bat", targetDir, onOutput);
            }
            else
            {
                onOutput("init.bat not found; you can run it manually later.");
            }

            return code;
        }

        public Task<int> UpAsync(Action<string> onOutput) => RunVagrantAsync("up", onOutput);

        public Task<int> HaltAsync(Action<string> onOutput) => RunVagrantAsync("halt", onOutput);

        public Task<int> StatusAsync(Action<string> onOutput) => RunVagrantAsync("status", onOutput);

        public Task<int> ReloadAsync(Action<string> onOutput) => RunVagrantAsync("reload --provision", onOutput);

        /// <summary>
        /// Reads the current machine state without writing to the log.
        /// </summary>
        public async Task<HomesteadStatus> GetStatusAsync()
        {
            var lines = new List<string>();
            await RunVagrantAsync("status --machine-readable", line =>
            {
                lock (lines) lines.Add(line);
            });
            return HomesteadStatus.Parse(lines);
        }

        private async Task<int> RunVagrantAsync(string arguments, Action<string> onOutput)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "vagrant",
                    Arguments = arguments,
                    WorkingDirectory = Environment.Directory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true })
                {
                    process.OutputDataReceived += (s, e) => { if (e.Data != null) onOutput(e.Data); };
                    process.ErrorDataReceived += (s, e) => { if (e.Data != null) onOutput(e.Data); };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    await Task.Run(() => process.WaitForExit());
                    return process.ExitCode;
                }
            }
            catch (Exception ex)
            {
                onOutput("ERROR: " + ex.Message);
                onOutput("Make sure Vagrant is installed and available on your PATH.");
                return -1;
            }
        }

        private async Task<int> RunProcessAsync(string fileName, string arguments,
            string workingDirectory, Action<string> onOutput)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true })
                {
                    process.OutputDataReceived += (s, e) => { if (e.Data != null) onOutput(e.Data); };
                    process.ErrorDataReceived += (s, e) => { if (e.Data != null) onOutput(e.Data); };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    await Task.Run(() => process.WaitForExit());
                    return process.ExitCode;
                }
            }
            catch (Exception ex)
            {
                onOutput("ERROR: " + ex.Message);
                return -1;
            }
        }
    }
}
