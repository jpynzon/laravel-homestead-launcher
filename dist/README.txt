Homestead Launcher
==================

Start and control a Laravel Homestead box, with a built-in SSH terminal
and a notepad — all in one window.

How to run
----------
1. Keep HomesteadLauncher.exe and HomesteadLauncher.exe.config together
   in the same folder (a folder you can write to).
2. Double-click HomesteadLauncher.exe.

On first launch it automatically searches common locations for your
Homestead folder (the one with a Vagrantfile). If it can't find one, use:
  - "Locate Homestead..." to pick the folder yourself, or
  - "Clone new Homestead" to download it with Git.
Once found, the path is remembered for next time.

Requirements
------------
- Windows 10 / 11 (includes .NET Framework 4.7.2)
- Vagrant installed and on your PATH
- Git on your PATH (only needed for the "Clone new Homestead" button)

Notes
-----
- The app creates two small files next to the exe: "homestead.path"
  (remembered Homestead location) and "notes.last" (last notes file).
