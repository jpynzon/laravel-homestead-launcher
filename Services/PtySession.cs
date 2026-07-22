using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using HomesteadLauncher.Services.ConPty;
using static HomesteadLauncher.Services.ConPty.ConPtyInterop;

namespace HomesteadLauncher.Services
{
    /// <summary>
    /// A real pseudo-terminal session. Spawns a command attached to a Windows
    /// Pseudo Console (ConPTY) so the child sees a genuine terminal: programs
    /// like <c>ls</c> emit columns/colors and the shell prints a live prompt.
    /// Output bytes (including VT/ANSI escape sequences) are decoded to chars
    /// and raised via <see cref="DataReceived"/>; keystrokes are sent with
    /// <see cref="Write"/>.
    /// </summary>
    public class PtySession
    {
        private IntPtr hPC = IntPtr.Zero;
        private IntPtr attrList = IntPtr.Zero;
        private PROCESS_INFORMATION procInfo;
        private FileStream input;   // we write here -> child stdin
        private FileStream output;  // child stdout -> we read here
        private Thread readThread;
        private volatile bool running;

        /// <summary>Raised (on a background thread) with decoded output chars.</summary>
        public event Action<char[], int> DataReceived;

        /// <summary>Raised once (on a background thread) when the session ends.</summary>
        public event Action Exited;

        public bool IsRunning => running;

        public void Start(string workingDirectory, string command, int cols, int rows)
        {
            // Advertise a capable terminal type to the remote shell.
            Environment.SetEnvironmentVariable("TERM", "xterm-256color");

            // inputRead/Write: we keep the write end; the pty reads the read end.
            // outputRead/Write: the pty writes the write end; we read the read end.
            if (!CreatePipe(out SafeFileHandle inputRead, out SafeFileHandle inputWrite, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (input) failed");
            if (!CreatePipe(out SafeFileHandle outputRead, out SafeFileHandle outputWrite, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (output) failed");

            var size = new COORD { X = (short)Math.Max(1, cols), Y = (short)Math.Max(1, rows) };
            int hr = CreatePseudoConsole(size, inputRead, outputWrite, 0, out hPC);
            if (hr != 0)
                throw new Win32Exception(hr, "CreatePseudoConsole failed (needs Windows 10 1809+)");

            // The pty duplicated these; our copies are no longer needed.
            inputRead.Dispose();
            outputWrite.Dispose();

            input = new FileStream(inputWrite, FileAccess.Write);
            output = new FileStream(outputRead, FileAccess.Read);

            StartProcess(workingDirectory, command);

            running = true;
            readThread = new Thread(ReadLoop) { IsBackground = true, Name = "pty-read" };
            readThread.Start();
        }

        private void StartProcess(string workingDirectory, string command)
        {
            // Size the attribute list, allocate it, then attach the pseudoconsole.
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attrList = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed");

            if (!UpdateProcThreadAttribute(attrList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed");

            var startupInfo = new STARTUPINFOEX();
            startupInfo.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            startupInfo.lpAttributeList = attrList;

            bool ok = CreateProcess(
                null,
                command,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                EXTENDED_STARTUPINFO_PRESENT,
                IntPtr.Zero,
                string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory,
                ref startupInfo,
                out procInfo);

            if (!ok)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess failed");
        }

        private void ReadLoop()
        {
            var buffer = new byte[4096];
            var decoder = Encoding.UTF8.GetDecoder();
            var chars = new char[8192];

            try
            {
                int read;
                while (running && (read = output.Read(buffer, 0, buffer.Length)) > 0)
                {
                    int count = decoder.GetChars(buffer, 0, read, chars, 0, false);
                    if (count > 0)
                    {
                        var copy = new char[count];
                        Array.Copy(chars, copy, count);
                        DataReceived?.Invoke(copy, count);
                    }
                }
            }
            catch
            {
                // Pipe closed as the child exits — fall through to Exited.
            }

            running = false;
            Exited?.Invoke();
        }

        /// <summary>Sends text (keystrokes / control sequences) to the session.</summary>
        public void Write(string data)
        {
            if (!running || input == null || string.IsNullOrEmpty(data)) return;
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(data);
                input.Write(bytes, 0, bytes.Length);
                input.Flush();
            }
            catch
            {
                // Session may have just ended; ignore.
            }
        }

        public void Resize(int cols, int rows)
        {
            if (hPC != IntPtr.Zero)
            {
                ResizePseudoConsole(hPC, new COORD
                {
                    X = (short)Math.Max(1, cols),
                    Y = (short)Math.Max(1, rows)
                });
            }
        }

        public void Stop()
        {
            running = false;

            // Closing the pseudoconsole signals the child to exit.
            try { if (hPC != IntPtr.Zero) ClosePseudoConsole(hPC); } catch { }
            hPC = IntPtr.Zero;

            try { input?.Dispose(); } catch { }
            try { output?.Dispose(); } catch { }

            try { if (procInfo.hThread != IntPtr.Zero) CloseHandle(procInfo.hThread); } catch { }
            try { if (procInfo.hProcess != IntPtr.Zero) CloseHandle(procInfo.hProcess); } catch { }
            procInfo = default;

            if (attrList != IntPtr.Zero)
            {
                try { DeleteProcThreadAttributeList(attrList); } catch { }
                try { Marshal.FreeHGlobal(attrList); } catch { }
                attrList = IntPtr.Zero;
            }
        }
    }
}
