# FlipFlop 1.0.3

Typed in the wrong layout (`akuo` instead of `שלום`)? Tap **Shift twice**. FlipFlop finds where the wrong-layout text starts, retypes it in the other layout, and switches your keyboard. Tap again to undo.

## Download
**[Download the latest FlipFlop zip](https://github.com/WhiteUmbr3lla/FlipFlop/releases/latest)**, unzip it to a folder you'll keep, and run `FlipFlop.exe`. Needs Windows 10/11 with English and Hebrew keyboard layouts installed.

FlipFlop isn't code-signed, so Windows may say "Windows protected your PC": click **More info**, then **Run anyway**. Every release zip is built from this repo's source by [GitHub Actions](.github/workflows/release.yml), not on anyone's PC, and you can check that yourself:

    gh attestation verify FlipFlop-<version>.zip -R WhiteUmbr3lla/FlipFlop

Privacy: what you type stays in memory and is never saved or sent anywhere. See [PRIVACY.md](PRIVACY.md).

## What gets fixed (Smart mode)
1. **Text selected** (mouse or Shift+arrows): converts exactly the selection. Use this to force it.
2. **You were just typing**: it finds the run of words before the cursor that reads best converted, judging the run as a whole. So `to ha kl` becomes `אם יש לך` even though "to" is a real English word, while `i want to` + Hebrew keeps "to". Unknown words, like names, join the run when they sit next to it.
3. **Neither**: works on the current line (Shift+Home), same detection.

Settings (double-click the tray icon): the hotkey (double Shift or any combo), what the hotkey fixes (Smart / Selected text only / Last word only / Everything since last click), auto-fix while typing, start with Windows, and a troubleshooting log (off by default, with a Clear log button).

**Auto-fix while typing** (Settings or tray menu, off by default; it's separate from what the hotkey fixes): when you finish a word with Space and it's clearly in the wrong layout, FlipFlop fixes it (and the wrong words before it) and switches the keyboard right away. It only fires when the converted word is a common word and the typed one isn't, so words that are real both ways (`to`/`אם`), names and typos are left to the hotkey. Tap Shift twice right after an auto-fix to undo it; auto mode then leaves the rest of that run alone. It never acts in password fields (detected through UI Automation, browsers included) or terminals.

**Stays out of the way:** right-click the tray icon, then "Don't use in <app>" for the app you were last typing in (code editors, games, remote desktop...), or "Excluded apps..." to edit the list (`%AppData%\FlipFlop\excluded.txt`). FlipFlop also pauses by itself in full-screen games, videos and presentations, including borderless full-screen windows (but not in maximized ones).

**Starts with Windows** by default (set on first launch, with a one-time tray notification). If you move the folder, the startup entry follows the new location the next time you run it. Turn it off in Settings. (The Microsoft Store version uses a Windows startup task instead; manage it in Windows Settings > Apps > Startup.)

**My words** (tray menu): names, slang, anything the built-in word lists don't know. Spell each one correctly, one per line. Saved in `%AppData%\FlipFlop\words.txt`.

## For developers

### Build
Needs only .NET Framework 4.8, which ships with Windows 10/11:

    build.cmd
    FlipFlop.exe --selftest | more

The self-test runs about 220 checks: real Hebrew and English sentences typed on the wrong layout, alone and after correct text, plus auto mode simulated word by word (it must fix every wrong sentence and never fire on correct ones).

End-to-end test (FlipFlop must be running with default settings; hands off the keyboard while it runs, about a minute):

    csc /target:winexe /codepage:65001 /out:tests\e2e.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll tests\e2e.cs
    tests\e2e.exe        (results in tests\e2e.log)

It types into its own window like a keyboard and checks fixing, undo, selection, the current-line fallback, clipboard restore (text, files, images) and the layout switch, in both a plain and a rich edit box. `tests\e2e.exe --excluded` checks an excluded app is left alone (put `e2e.exe` in `excluded.txt` and turn auto mode on first). `tests\e2e.exe --auto` runs the auto mode cases instead (turn "Auto-fix while typing" on first), including a password box.

`package.cmd` rebuilds, runs the self-test, and zips `FlipFlop.exe` with the user guide (`package\README.txt`) into `dist\FlipFlop-<version>.zip` for sharing. The version lives in the `AssemblyFileVersion` line at the top of `Program.cs`; bump it before sharing a new build.

`store\make-msix.ps1` builds the Microsoft Store package (`dist\FlipFlop-<version>.msix`, unsigned; the Store signs it) from `store\AppxManifest.xml`; it needs `makeappx.exe` from the Windows SDK. With `-LayoutOnly` it only lays out `msix\`, which you can install for testing with `Add-AppxPackage -Register msix\AppxManifest.xml` (Developer Mode). Pushing a `v*` tag makes GitHub Actions build, attest and attach both the zip and the .msix to the release.

`build.cmd` builds twice: the first build draws the logo into `flipflop.ico`, the second uses it as the exe icon.

Requires both an English and a Hebrew keyboard layout installed.

## Known limits
- Windows blocks injected input into apps running as administrator. Use the tray menu's "Restart as admin" option.
- Autocomplete fields (browser address bar, IDE suggestions) can throw off the backspace count. Select the text and use the hotkey instead.
- VS Code copies the whole line on Ctrl+C with no selection, so the selection path can misfire there. Fixing what you just typed is unaffected.
- Fixing a selection or line reads it with Ctrl+C, then types the result over it (multi-line text is pasted instead, since a typed line break could send a chat message). The clipboard is snapshotted and put back afterwards (copied files, images, formatted text). If nothing is selected, it isn't touched.
- If a hotkey press seems to do nothing, tick "Write a log" in Settings and try again: `%TEMP%\FlipFlop.log` shows which app it happened in, which path it took, when FlipFlop paused (excluded app or full screen) and any error. It never contains what you typed.
- Terminals are skipped for selection/line fixes (Ctrl+C there would interrupt the running program) and for auto mode.
- Antivirus software may flag FlipFlop: it reads the keyboard and types for you, which is also what keyloggers do, and it isn't code-signed. It never sends anything anywhere.
- Keystrokes are held in memory only, never written to disk, and are cleared on every click, Enter, arrow key, or window switch.

Word lists: [FrequencyWords](https://github.com/hermitdave/FrequencyWords) by Hermit Dave, CC-BY-SA 4.0.
