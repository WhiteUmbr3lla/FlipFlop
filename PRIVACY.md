# FlipFlop privacy policy

FlipFlop fixes text typed in the wrong keyboard layout. To do that it watches the keys you press, and this is everything it does with them:

- **What you type stays in memory.** FlipFlop keeps the last few words you typed so it can retype them in the other layout. They're cleared on every click, Enter, arrow key or window switch, and never written to disk.
- **No network.** FlipFlop never connects to the internet. Nothing you type, and nothing about you or your computer, is sent anywhere. There are no analytics, ads or accounts.
- **Password fields are skipped.** Auto-fix never acts in password fields, which it detects through Windows UI Automation.
- **The clipboard is put back.** When fixing a selection, FlipFlop briefly uses the clipboard and then restores what was on it.
- **What it saves:** your settings (in the Windows registry), your "My words" and excluded-apps lists, and an optional troubleshooting log. The log is off by default. It records which app a fix happened in and which method was used, never what you typed, and you can delete it with "Clear log" in Settings. All of this stays on your PC.

Questions: open an issue at https://github.com/WhiteUmbr3lla/FlipFlop/issues.
