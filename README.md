# FriendList Frenzy

**A modern, upgraded FFXIV Friend List manager for Dalamud — featuring instant search, custom groups, nicknames, color tags, notes, Last Seen tracking, protected favorites, and safe Bulk Friend Removal.**

---

## Key Features

- **Bulk Friend Removal (Batch Queue):** Select multiple friends (or click *Select Offline* / *Select Visible*) and remove them automatically one by one at a safe, configurable pace — with live progress bar, Pause/Resume, Cancel, and triple safety verification (ContentId + ContextMenu label + exact target name check in FFXIV's confirmation dialog).
- **Protected Favorites (`★`):** Star important friends to lock them so they can never be accidentally selected for bulk removal.
- **Custom Groups & Color Coding:** Create unlimited groups (e.g. *Favorites*, *Free Company*, *Static*, *To Remove*) with custom colors, plus per-friend custom name colors.
- **Nicknames & Personal Notes:** Remember who everyone is with custom nicknames and hoverable notes.
- **Automatic Last Seen Online Tracking:** Passively records when each friend was last seen online so you can easily spot inactive entries.
- **Fast Search & Filtering:** Filter by Name, Nickname, World, FC Tag, Zone, Note, Online/Offline status, Protected status, or Custom Group.
- **Bilingual UI (EN / PT-BR):** Defaults to English with instant one-click switching to Portuguese.

---

## Installation (Dalamud Custom Repository)

Add the unified **n0way02** Dalamud repository in `/xlsettings` → **Experimental** → **Custom Plugin Repositories**:

```text
https://raw.githubusercontent.com/n0way02/DalamudPlugins/main/pluginmaster.json
```

Then open `/xlplugins`, search for **FriendList Frenzy**, and click **Install**.

---

## Commands

- `/flf` or `/friendlistfrenzy` — Open/close FriendList Frenzy.
