[![C#](https://img.shields.io/badge/language-C%23-blue)]()
[![platform](https://img.shields.io/badge/platform-Windows-00adef)]()
[![size](https://img.shields.io/badge/size-10KB-4caf50)]()

# White Widow

A little trojan that tries not to be noticed.

It boots, checks if it's in a sandbox or a VM, and if it is, it just sits there and quits — no network, no trace. On a real machine it does the opposite: it hides in `explorer.exe`, makes itself start at login two ways (a registry key and a scheduled task), and then quietly sends what it sees to a server every minute.

Why it's hard to catch:
- it never writes its body to disk — it decrypts and runs in memory
- it calls Windows system functions directly, so tools that patch the usual hooks miss it
- it checks cores, RAM, how long the PC was off, the username, and the BIOS name to spot a fake machine

Files here: `WhiteWidow.cs` (the code) and `WhiteWidow.exe` (the built thing).

Run it on a real PC, not a VM — otherwise it'll just sleep 45 seconds and leave.