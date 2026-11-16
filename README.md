<p align="center">
  <img src="docs/icon.png" width="128" height="128" alt="TeleSave icon">
</p>

<h1 align="center">TeleSave</h1>

<p align="center">
  <strong>Drop a file on your menu bar. It lands in Telegram.</strong><br>
  A tiny macOS menu bar app that uploads anything you drag onto it to your Telegram Saved Messages — or any chat, group or channel you choose.
</p>

<p align="center">
  <a href="../../releases/latest"><strong>Download for macOS</strong></a> · macOS 15 or later · Apple Silicon & Intel
</p>

---

## Why TeleSave?

Telegram's Saved Messages is the cloud drive you already use: it's on every device you own and it keeps files up to 2 GB (4 GB with Premium). Getting a file there from your Mac, though, still means opening Telegram, finding the chat, and dragging the file in.

TeleSave cuts that down to one move: **drag the file up to your menu bar** and drop it on the shelf that slides out under the paper plane. That's it. The upload runs in the background, and the file is waiting on your phone a moment later.

## Features

### Drag, drop, done
- Drag files toward the **menu bar** and a drop shelf appears right under the TeleSave icon. Or drop them into the panel that opens when you click the icon.
- Prefer the keyboard? **Copy files in Finder and press ⌘V** in the panel. Copied images and screenshots paste too.
- Or right-click files in Finder and choose **Send to TeleSave** (can be turned off in Settings).
- Prefer clicking? **Choose files…** works too.
- Drop a **folder** and send it as a single zip, or send every file inside it, your choice.
- A progress percentage right in the menu bar, plus upload speed and time remaining in the panel.

### Original quality, by default
- Files are sent as **documents**, so photos and videos keep their original resolution and file names.
- Want Telegram's compressed, inline media instead? It's a single toggle.
- Optionally send files dropped together as an **album** (up to 10).

### Send it anywhere
- **Saved Messages** by default, or pick a **person, group, channel, or forum topic**.
- TeleSave checks your permissions before you pick: you can only choose channels where you're allowed to post and groups that allow files.
- When the target isn't Saved Messages, it's **highlighted in orange**, so files never go to the wrong chat by accident.
- Your last five destinations stay one click away.

### Built for big files and bad Wi-Fi
- A real upload queue: several uploads at once, cancel or retry any of them.
- **Survives interruptions.** Lose the network and uploads pause; reconnect and they continue. Quit the app and unfinished uploads pick up where they left off.
- Handles Telegram's rate limits automatically by waiting and trying again.
- Keeps your Mac awake while a large upload is running.

### Your files at a glance
- Every upload shows a **Quick Look preview**, its size, and when it was uploaded.
- **Download** any uploaded file back to your Downloads folder, or **delete** it from Telegram, right from the list.
- **Browse the whole chat:** the list shows every file in the selected chat, not just TeleSave's uploads, with accent- and case-insensitive search by file name or caption, and filters for documents, images, videos and audio (images and videos include the ones sent in original quality). Prefer to see only your TeleSave uploads? Switch it off in Settings.
- A notification tells you when your uploads are done.

### Feels native
- Lives quietly in the menu bar: no Dock icon, no extra windows.
- Light and dark mode, VoiceOver labels, launch at login.

## Privacy and security

- **Your files go directly from your Mac to Telegram.** There is no TeleSave server in between, and nothing is uploaded anywhere else.
- You sign in with Telegram's own login (**QR code** or phone number + code, with two-step verification supported). TeleSave never sees or stores your Telegram password.
- Your session is stored **encrypted** on your Mac, with the key kept in the macOS Keychain.
- TeleSave runs in the macOS **App Sandbox**: it can only read the files you give it, and it can only write to your Downloads folder when you ask it to download a file.
- Sign out at any time from Settings. Signing out removes the local session data.

> TeleSave may download a small, cryptographically signed configuration file (for example, to announce a new version). It contains no personal data, and nothing about you or your files is sent.

## Getting started

1. **Download** TeleSave and drag it into your Applications folder.
2. **Open it.** A paper plane appears in your menu bar.
3. **Sign in** by scanning the QR code with Telegram on your phone (*Settings › Devices › Link Desktop Device*), or with your phone number.
4. **Drag a file** up to the menu bar and drop it on the shelf that appears. Look for it in Saved Messages.

## FAQ

**Is this an official Telegram app?**
No. TeleSave is an independent app built on Telegram's official open-source client library (TDLib). It signs in to your own account, just like Telegram Desktop does.

**Why does it need to sign in as me? Couldn't a bot do this?**
Telegram bots can't post to your Saved Messages, and they are limited to 50 MB files. Signing in as you is the only way to reach your own Saved Messages, with the full 2 GB / 4 GB limit.

**How big can my files be?**
Up to 2 GB per file, or 4 GB with Telegram Premium. TeleSave detects your plan and tells you before uploading if a file is too large.

**Will my photos be compressed?**
Not by default: everything is sent as a document, in original quality. You can switch to compressed media in Settings.

**What happens if my Mac goes to sleep or I lose Wi-Fi?**
TeleSave keeps the Mac awake while uploading. If the connection drops, uploads pause and continue automatically when it comes back, even after a restart.

**Does it work on Windows or Linux?**
Not yet. TeleSave is macOS-only for now; Windows and Linux versions are planned.

## Requirements

- macOS 15 Sequoia or later
- Apple Silicon or Intel Mac
- A Telegram account

## For developers

Build the macOS app with `scripts/build-app.sh` (output: `dist/TeleSave.app`) and package a DMG with `scripts/make-dmg.sh`. Run the tests with `swift test`, and `swift run TeleSaveSmoke` to check the TDLib integration against Telegram. An API key and the remote config can be bundled at build time through `Config/Secrets.env` (see `Config/Secrets.env.example`); remote config files are signed with `scripts/remote-config.swift`.

---

<sub>TeleSave is not affiliated with, endorsed by, or sponsored by Telegram. Telegram is a trademark of its respective owner.</sub>
