# ASD-Extensions

Extensions for **ASD**. One folder per extension: `Extensions/<Publisher>/<Name>/`.

```
Extensions/MhX2780/Pomodoro/
  Pomodoro.csproj        a small class library
  PomodoroExtension.cs      implements IAsdExtension (Register adds pages)
  extension.json          everything ASD needs to know BEFORE the DLL is loaded
  icon.png                (optional) icon for the store list and the install window
  LICENSE.txt             (optional) shown in the install window
  screenshots/*.png       (optional)
Shared/ASD.Extensibility/ the contract (interfaces) between ASD and an extension
tools/build-extensions.ps1 builds everything -> dist/extensions.zip + dist/extensions.txt
```

## Extensions in this store
| Extension | What it does |
|---|---|
| Pomodoro | Work/break timer; keeps counting while you use other tools and notifies you |
| Unit Converter | Length, mass, temperature, data size, time, speed |
| Color Tools | HEX / RGB / HSL with sliders and a preview; copy buttons |
| Quick Notes | Notes that save themselves (one .txt file each, in the extension's data folder) |
| Date Calculator | Time between two dates; a date plus or minus days |
| Checklist | A to-do list that is saved between runs |
| Python Compiler | Write and run Python code with input, output and errors (needs Python 3) |
| Python to EXE | Turns a .py file into an exe with PyInstaller (installs it when missing) |
| Calendar | Month calendar with events; events with a time notify you |

To add one: copy any folder under `Extensions/MhX2780/`, rename it, change `extension.json` (new `id`, `assembly` = the project name) and write your page. The build finds it by itself.

## How it is published
Every push to `main` runs **Build extensions** (GitHub Actions). It builds each extension and publishes two files to the fixed release **extensions**:

| file | what |
|---|---|
| `extensions.txt` | tiny list. Line 1 = SHA-256 of the zip, then `id \| version \| name \| description` per extension |
| `extensions.zip` | one folder per extension: its DLL, extension.json, icon, license, screenshots |

ASD reads `extensions.txt` (at most once a day); when something is new or newer it downloads `extensions.zip`, checks the hash, and shows **New** / **Update** in its Extensions page.

## extension.json
```json
{
  "id": "Publisher.Name",
  "name": "Shown name",
  "version": "1.0.0",
  "author": "...",
  "description": "One line (no | character).",
  "assembly": "Name.dll",
  "apiVersion": 1,
  "minAsdVersion": "1.0.0",
  "glyph": "",
  "simpleIcon": "dropbox",
  "defaultPlacement": "more",
  "requires": { "extensions": [], "programs": ["ffmpeg"] }
}
```
- Icon: `icon.png` (store and install window), otherwise `simpleIcon` (a Simple Icons name), otherwise `glyph` (a Segoe Fluent Icons character).
- `defaultPlacement`: `main` (navigation bar) or `more` (More tools). The person can change it while installing.

## Rules for an extension
- Build the UI in code (no XAML files).
- Reference `ASD.Extensibility` and Windows App SDK as compile-only (see Pomodoro.csproj) and never ship them.
- Create windows and controls on the UI thread.
- Only add to the contract (IAsdHost), never change or remove a member.
- An extension runs inside ASD with the same permissions: publish only code you trust.
