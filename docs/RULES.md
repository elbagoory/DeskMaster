# Writing cleanup rules

Every cleanup item in DeskMaster is a JSON rule. You don't need to write C# to add one:

- **Built-in rules** live in [`src/DeskMaster.Core/Data/cleanup-rules.json`](../src/DeskMaster.Core/Data/cleanup-rules.json). Send a pull request to share a rule with everyone.
- **Your own rules** go in any `*.json` file in the user rules folder. DeskMaster loads them at startup, and a rule with the same `id` replaces the built-in one:
  - Windows: `%APPDATA%\DeskMaster\rules\`
  - macOS: `~/Library/Application Support/DeskMaster/rules/`
  - Linux: `~/.config/DeskMaster/rules/`

Each file holds a JSON array of rules. Comments (`//`) and trailing commas are allowed.

## Example

```jsonc
[
  {
    "id": "app-slack-cache",                 // unique, kebab-case
    "name": "Slack cache",                   // shown in the list
    "description": "Cached images and files from Slack. You stay signed in.",
    "category": "Apps",                      // System, Drivers, Browsers, Developer tools, Editors, Apps, AI & models
    "safety": "safe",                        // "safe" (pre-selected) or "caution" (user must opt in)
    "closeProcesses": ["slack"],             // skipped while these are running
    "paths": {
      "windows": ["{AppData}/Slack/Cache", "{AppData}/Slack/Code Cache"],
      "macos":   ["{Config}/Slack/Cache"],
      "linux":   ["{Config}/Slack/Cache"]
    }
  }
]
```

## Fields

| Field | Default | Meaning |
|---|---|---|
| `id` | required | Unique id. Users can select it in the CLI with `--ids`. |
| `name`, `description` | required | Plain language: what it is and what happens after cleaning. |
| `category` | `Other` | Groups rules in Pro mode. |
| `safety` | `safe` | `caution` = has a side effect (re-downloads, slower first launch). It's never pre-selected. |
| `requiresAdmin` | `false` | Only offered when DeskMaster runs elevated. |
| `paths` | `{}` | OS key (`windows`, `macos`, `linux`, or `all`) mapped to path templates. |
| `platforms` | from `paths` | Explicit OS list, for rules that only run a command. |
| `mode` | `contents` | `contents` empties the folder and keeps it. `self` deletes the matched file or folder. |
| `minAgeHours` | `0` | Only delete files that haven't been modified for this long. |
| `include` | all files | File name patterns, such as `["*.log", "thumbcache_*.db"]`. |
| `closeProcesses` | none | Process names that must be closed first. |
| `command` | none | `{ "file": "npm", "args": ["cache", "clean", "--force"] }`. If it's installed, it runs instead of deleting files. If it fails, DeskMaster deletes the files in `paths` instead. |
| `estimateOnly` | `false` | `paths` are used only to estimate the size, and the `command` does the cleaning (e.g. hiberfil.sys). |
| `handler` | none | A built-in special cleaner: `recycle-bin` or `wsl-compact`. |
| `notes` | none | Extra detail shown in Pro mode. |

## Path templates

| Token | Windows | macOS | Linux |
|---|---|---|---|
| `{Home}` | `C:\Users\you` | `/Users/you` | `/home/you` |
| `{Temp}` | `%TEMP%` | `$TMPDIR` | `/tmp` |
| `{Cache}` | `%LOCALAPPDATA%` | `~/Library/Caches` | `$XDG_CACHE_HOME` or `~/.cache` |
| `{Config}` | `%APPDATA%` | `~/Library/Application Support` | `$XDG_CONFIG_HOME` or `~/.config` |
| `{Data}` | `%LOCALAPPDATA%` | `~/Library/Application Support` | `$XDG_DATA_HOME` or `~/.local/share` |
| `{LocalAppData}` `{AppData}` `{ProgramData}` `{WinDir}` `{SystemDrive}` | Windows only | – | – |
| `{Library}` | – | `~/Library` | – |

- Use forward slashes. `*` and `?` work inside any path segment, e.g. `{LocalAppData}/Google/Chrome/User Data/*/Cache`.
- `%VARIABLE%` environment variables are expanded too.
- A template that uses a token this OS doesn't have is ignored.

## Safety rules (enforced in code, whatever a rule says)

- Drive roots, top-level folders, your home folder and its main folders (Desktop, Documents, Downloads, ...), Program Files, the Windows folder and system folders on macOS and Linux can never be cleaned.
- Links (symlinks and junctions) are never followed.
- Files that are in use are skipped, never forced.
- Nothing is deleted without the user confirming it.

## Translations

Rule names and descriptions are translated in `src/DeskMaster.Core/Data/translations-ar.json`, keyed by rule id:

```json
"rules": { "app-slack-cache": { "name": "ذاكرة Slack", "description": "..." } }
```

A test fails when a built-in rule has no Arabic translation. Rules without a translation are shown in English.

## Checklist for a new rule

1. Would a normal user lose anything they care about? If yes, it's not a cleanup rule.
2. Will the app rebuild or re-download the data by itself? Say so in `description`.
3. Can the app corrupt the data while it's running? Add `closeProcesses`.
4. Add the Arabic name and description to `translations-ar.json`.
5. Run `dotnet test`. The catalog tests check that ids are unique, every rule is valid, and every rule is translated.
6. Run `deskmaster-cli analyze --all` and check your rule finds the right folders.
