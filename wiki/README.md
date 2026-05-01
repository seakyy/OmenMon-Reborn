# Wiki source files

This folder contains the Markdown source for the [OmenMon-Reborn GitHub Wiki](../../wiki).

| File | Wiki page |
|------|-----------|
| `Home.md` | Home |
| `Architecture.md` | Architecture |
| `Model-Database.md` | Model Database |
| `Auto-Detection.md` | Auto-Detection |
| `Contributing-Hardware-Data.md` | Contributing Hardware Data |

## Images

Place screenshot files in `wiki/images/`. The four screenshots referenced across the pages are:

| Filename | Used in | Shows |
|----------|---------|-------|
| `unknown-model-detected.png` | Auto-Detection | The startup prompt for an unrecognised device |
| `auto-config-saved.png` | Auto-Detection | The success dialog after heuristic detection |
| `contribute-menu-item.png` | Contributing Hardware Data | The tray context menu with the new item visible |
| `hardware-dump-copied.png` | Contributing Hardware Data | The confirmation dialog after the dump is copied |

## Publishing to GitHub Wiki

GitHub Wiki is a separate Git repository at `https://github.com/<user>/<repo>.wiki.git`.  
To push these files there:

```bash
git clone https://github.com/<user>/OmenMon-Reborn.wiki.git
cp wiki/*.md OmenMon-Reborn.wiki/
cp -r wiki/images OmenMon-Reborn.wiki/
cd OmenMon-Reborn.wiki
git add .
git commit -m "Add developer wiki"
git push
```
