# Homestead Launcher

A small Windows desktop app for driving a [Laravel Homestead](https://laravel.com/docs/homestead) development box without living in the terminal. Start/stop the machine, see its live status, open a real SSH terminal, edit your `hosts` and `Homestead.yaml` files, and keep a scratch notepad for things like database credentials — all in one window.

Built with C# / WPF on .NET Framework 4.7.2.

> Personal/learning project. It shells out to your existing `vagrant` and `git`; it does not bundle or replace them.

---

## Features

- **Live machine status** — on launch it runs `vagrant status` and shows whether the box is **Running**, **Stopped**, **Suspended**, or **Not created**, with a colored beacon. Start/Stop/SSH enable and disable based on the real state.
- **Machine controls** — Start (`vagrant up`), Stop (`vagrant halt`), and Reload &amp; provision (`vagrant reload --provision`), with output streamed into the console.
- **Built-in SSH terminal** — a real pseudo-terminal (Windows ConPTY) running `vagrant ssh` *inside* the app: proper columns, colors, a live prompt, and interactive programs. No separate terminal window.
- **Notepad panel** — a side-by-side notepad for `.txt` files (Open / Save / Save as / Ctrl+S). It remembers your last file and reloads it next launch, so you can keep DB credentials beside the terminal.
- **Auto-locate Homestead** — finds your Homestead folder automatically across common locations and remembers it, so there's no config to edit — even on a different PC.
  - **Locate Homestead…** — pick the folder yourself if it isn't found.
  - **Clone new Homestead** — don't have it yet? Clone the official repo with Git in one click.
- **Config editors** — edit the Windows `hosts` file and `Homestead.yaml` in-app. Saving `hosts` automatically elevates via a UAC prompt (since it needs administrator rights). A "Saved" toast confirms success.

---

## Prerequisites

To run the app you need:

- **Windows 10 or 11** (the .NET Framework 4.7.2 runtime is already included).
- **[Vagrant](https://www.vagrantup.com/)** installed and on your `PATH` — the app calls `vagrant` for all machine actions.
- A **Vagrant provider** — [VirtualBox](https://www.virtualbox.org/) is Homestead's default.
- **[Git](https://git-scm.com/)** on your `PATH` — only required for the **Clone new Homestead** button.
- **Laravel Homestead** itself — either already set up, or use the in-app clone button to get it.

To build from source you also need:

- **Visual Studio 2022** with the *.NET desktop development* workload, **or** the **.NET Framework 4.7.2 Developer Pack** + MSBuild.

---

## Getting started

### Option A — run the prebuilt app

1. Download the two files from [`dist/`](dist):
   - `HomesteadLauncher.exe`
   - `HomesteadLauncher.exe.config`
2. Keep them **together in the same folder** (one you can write to).
3. Double-click `HomesteadLauncher.exe`.

On first launch it searches common locations for your Homestead folder. If it can't find one, use **Locate Homestead…** or **Clone new Homestead** in the sidebar.

### Option B — build from source

Using Visual Studio:

1. Open `HomesteadLauncher.csproj` in Visual Studio 2022.
2. Set the configuration to **Release** and build (Ctrl+Shift+B).
3. The app is produced at `bin/Release/HomesteadLauncher.exe`.

Using MSBuild from the command line:

```bash
msbuild HomesteadLauncher.csproj /t:Rebuild /p:Configuration=Release
```

---

## Configuration

You normally don't need to configure anything — the app auto-locates Homestead and remembers the result.

- **`HomesteadLauncher.exe.config`** — optional. Set `HomesteadPath` to the folder that contains your Homestead `Vagrantfile` if you want to pin it; leave it blank to rely on auto-locate.

  ```xml
  <appSettings>
    <add key="HomesteadPath" value="" />
  </appSettings>
  ```

The app writes two small files next to the executable at runtime (both safe to delete):

- `homestead.path` — the remembered Homestead folder.
- `notes.last` — the path of the last notes file you opened.

---

## Using it

1. **Launch** — the status beacon shows the current machine state.
2. **Start machine** boots the box; **Stop machine** halts it. Buttons enable/disable to match the state.
3. **Open SSH terminal** — connects `vagrant ssh` in the console. Type directly; **Disconnect SSH** (or `exit`) ends it.
4. **Notes** (top-right) — expands a notepad beside the console for credentials/notes.
5. **Configure hosts / Configure Homestead.yaml** — edit those files in-app. Saving `hosts` prompts for administrator approval.

---

## Project structure

```
├─ App.xaml / App.xaml.cs          Application entry point
├─ MainWindow.xaml / .xaml.cs      Main console window and logic
├─ ConfigEditorWindow.xaml / .cs   hosts / Homestead.yaml editor (with admin save)
├─ Models/                         HomesteadEnvironment, HomesteadStatus
├─ Services/
│  ├─ LauncherService.cs           Runs vagrant, auto-locates, clones
│  ├─ PtySession.cs                ConPTY pseudo-terminal session
│  ├─ NotesService.cs              Notes file IO + last-file memory
│  └─ ConPty/ConPtyInterop.cs      Win32 interop for the Pseudo Console API
├─ Controls/
│  ├─ TerminalControl.cs           Terminal rendering + input
│  └─ VtScreen.cs                  VT100/xterm emulator (screen + parser)
├─ HomesteadLauncher.ico           App icon
└─ dist/                           Ready-to-share build (exe + config)
```

---

## Notes &amp; limitations

- The SSH terminal is a **pragmatic** VT emulator — everyday shell use, colors, and most TUI programs work well; some exotic escape sequences may not render perfectly.
- The notepad saves files as **plain text**, so treat any credentials you store accordingly.
- Editing the `hosts` file requires **administrator rights**; the app handles this with a one-time UAC prompt per save.

---

## Tech stack

C# · WPF · .NET Framework 4.7.2 · Windows Pseudo Console (ConPTY) · Vagrant CLI

## License

No license is set yet. If you want to allow others to reuse the code, add one (for example, [MIT](https://choosealicense.com/licenses/mit/)).
