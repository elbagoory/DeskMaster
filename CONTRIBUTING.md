# Contributing to DeskMaster

Thanks for helping! DeskMaster deletes files, so we value **safety and clarity** above everything else.

## Ways to help

- **Add a cleanup rule.** Edit `src/DeskMaster.Core/Data/cleanup-rules.json`. See [docs/RULES.md](docs/RULES.md).
- **Add a movable folder.** Edit `src/DeskMaster.Core/Data/relocations.json`. This works for any app that reads its data location from an environment variable.
- **Translate.** See below.
- **Test on your OS.** macOS and Linux reports are especially valuable.
- **Improve the UI.**

## Development

```bash
dotnet build
dotnet test tests/DeskMaster.Core.Tests     # engine
dotnet test tests/DeskMaster.App.Tests      # headless UI + translation coverage
dotnet run --project src/DeskMaster.App
```

- Keep logic in `DeskMaster.Core` and cover it with tests. The app and the CLI should stay thin.
- The UI uses Avalonia with compiled bindings (`x:DataType`) and CommunityToolkit.Mvvm.
- Never weaken `SafetyGuard`. If a rule needs an exception, make the rule more specific instead.

## Text and translations

English text is the translation key, so the source stays readable:

- XAML: `Text="{l:T 'Free up space'}"` (use `’` instead of `'` inside the text).
- C#: `T("Scan cancelled.")` for fixed text, `F("Freed {0}.", size)` for text with values. In the core library, use `Localization.Text.T` and `Text.F`.

To add a language, for example French:
1. Copy `src/DeskMaster.App/Localization/Arabic.cs` to `French.cs`, translate the values, and register it in `Loc.Initialize`.
2. Copy `src/DeskMaster.Core/Data/translations-ar.json` to `translations-fr.json` and translate the rule names and descriptions.
3. Add the language to `SettingsViewModel.Languages`.

The test `Every_ui_text_has_an_arabic_translation` fails when new text has no translation, and `Arabic_keeps_every_placeholder` fails when a placeholder such as `{0}` is lost. Copy them for your language.

## Pull request checklist

- [ ] `dotnet test` passes
- [ ] New rules have a plain-language `description` that says what happens after cleaning
- [ ] Rules with side effects use `"safety": "caution"`
- [ ] New UI text is wrapped in `T`/`F`/`{l:T}` and has an Arabic translation
- [ ] You ran `deskmaster-cli analyze --all` and the rule finds the right folders
