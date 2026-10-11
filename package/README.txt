FlipFlop 1.0 - fixes text typed in the wrong keyboard layout (Hebrew <-> English)
===============================================================================

Typed "akuo" when you meant "shalom" in Hebrew? Tap Shift twice and it's fixed.


SETUP
-----
1. Extract this zip to a folder you'll keep (for example Documents\FlipFlop).
2. Run FlipFlop.exe.
   Windows may show "Windows protected your PC", because the app isn't signed.
   Click "More info", then "Run anyway". You only have to do this once.
3. A flip-flops icon appears in the tray, next to the clock.
   It may be hidden under the ^ arrow; you can drag it out so it's always visible.
4. That's it. FlipFlop now starts with Windows by itself. If you don't want
   that, double-click the icon to open Settings and untick "Start with Windows".
   (Moved the folder later? Just run FlipFlop.exe from the new place once.)

You need Windows 10 or 11 with both a Hebrew and an English keyboard installed.


HOW TO USE
----------
- Typed gibberish? Tap Shift twice, quickly. FlipFlop finds where the wrong
  layout started, retypes that part correctly and switches your keyboard.
- Not what you wanted? Tap Shift twice again to undo.
- To fix one specific part, select it first, then tap Shift twice.
- Names or slang it doesn't know? Right-click the tray icon, choose "My words..."
  and add them, spelled correctly.
- Settings (double-click the tray icon): change the hotkey, choose what the
  hotkey fixes (smart detection, selection only, last word only, or everything
  since your last click), and turn auto mode on or off.


AUTO MODE (OPTIONAL)
--------------------
Tick "Auto-fix while typing" in Settings (or in the tray icon's right-click
menu). It works on its own, separately from the hotkey. Then, when you finish
a word with Space and it's clearly in the wrong layout, FlipFlop fixes it and
switches the keyboard by itself. It only acts when it's very sure: names, typos
and words that exist in both languages are left alone (use double Shift for
those). Didn't want that fix? Tap Shift twice right away to undo it.
It never touches password fields.


GOOD TO KNOW
------------
- Don't want FlipFlop in some app (a code editor, a game...)? While using that
  app, right-click the tray icon and choose "Don't use in <that app>".
  It also stays out of full-screen games by itself.
- Apps running as administrator can't be fixed unless FlipFlop runs as admin
  too. Right-click the tray icon and choose "Restart as admin".
- Privacy: FlipFlop keeps what you type in memory only, clears it constantly,
  and never saves it or sends it anywhere. It doesn't use the internet at all.
- Your antivirus might warn about FlipFlop. That's because it reads the keyboard
  and types for you (so do keyloggers) and it isn't signed. If you trust whoever
  gave it to you, you can allow it.
- Something didn't work? In Settings, tick "Write a log" and try again.
  %TEMP%\FlipFlop.log then shows what happened (it never contains what you
  typed). Send it to whoever gave you FlipFlop. "Clear log" deletes it.


UNINSTALL
---------
1. Open Settings and untick "Start with Windows", then click OK.
2. Right-click the tray icon and choose Exit.
3. Delete the FlipFlop folder.
Optional leftovers: the folder %AppData%\FlipFlop (your word lists) and the
registry key HKEY_CURRENT_USER\Software\FlipFlop (your settings).


Credits: the Hebrew and English word lists come from FrequencyWords by
Hermit Dave (https://github.com/hermitdave/FrequencyWords), CC-BY-SA 4.0.
