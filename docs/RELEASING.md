# OmenMon-Reborn — Release Runbook

A concrete, copy-paste-able checklist for cutting a release. Written for
the maintainer (seakyy), assumes a Windows machine with Git Bash + Visual
Studio 2022 installed in the usual places.

The first time you read this, do the whole flow on a *throwaway pre-release
tag* (e.g. `v1.4.0-rc1`) so any rough edges in the automation show up before
they bite a real release.

---

## TL;DR — every release, in order

```bash
# 1. Bump version
#    Edit: All/Version.cs  (1.4.0.0 → 1.5.0.0, plus the informational string)
#    Edit: wiki/Home.md    (current release line)
#    Edit: CHANGELOG.md    (move "Unreleased" to a new "## [X.Y.Z-reborn] - YYYY-MM-DD" block)

# 2. Build locally (Release config)
"/c/Program Files/Microsoft Visual Studio/2022/Enterprise/MSBuild/Current/Bin/MSBuild.exe" \
  -m -noLogo -restore -p:Configuration=Release -t:Rebuild OmenMon.sln

# 3. Run the tests
"/c/Program Files/dotnet/dotnet" test Tests/OmenMon.Tests/OmenMon.Tests.csproj \
  -c Release --nologo --no-build

# 4. Compute SHA256 of the shipped binary (paste into release notes)
"/c/Program Files/Git/usr/bin/sha256sum.exe" Bin/OmenMon.exe

# 5. Commit version bump + changelog, push
git add -A
git commit -m "Release v1.5.0"
git push origin master

# 6. Tag and push the tag — fires draft-release.yml
git tag v1.5.0
git push origin v1.5.0

# 7. Go to GitHub → Releases → edit the draft the workflow created
#    - Verify the auto-extracted notes look right
#    - Attach Bin/OmenMon.exe
#    - Add the SHA256 line from step 4
#    - Click "Publish release" — this fires release.yml which zips it
```

That's the whole flow. The rest of this doc is the **why** and the **what if**.

---

## Prerequisites (one-time)

You need these installed once:

- **Visual Studio 2022** (Enterprise / Professional / Community — any edition).
  Confirm MSBuild lives at:
  `C:\Program Files\Microsoft Visual Studio\2022\<edition>\MSBuild\Current\Bin\MSBuild.exe`
- **.NET 8 SDK** for `dotnet test`.
  Confirm with `dotnet --version` → should print `8.0.x`.
- **Git Bash** (or any sh-compatible shell) for the snippets above.
  Comes with Git for Windows.
- **PawnIO** installed from <https://pawnio.eu/> for runtime testing.
- **`gh` CLI** authenticated against the repo (`gh auth login`) **only if** you
  want to manage releases from the command line. Not required — the GitHub
  web UI works fine.

Verify the EmbeddedResource exists before any Release build:

```bash
ls -la Resources/LpcACPIEC.bin
```

If it's missing the Release build will fail with a clear error pointing to
`Resources/PAWN_BUILD.md`. Don't try to work around the error — fix the
missing file.

---

## Step 1 — Version bump

Three files need to know about the new version. Keep them in sync.

### `All/Version.cs`

```csharp
[assembly: AssemblyVersion("1.5.0.0")]
[assembly: AssemblyFileVersion("1.5.0.0")]
[assembly: AssemblyInformationalVersion("1.5.0-reborn")]
```

The CI workflow can override `AssemblyVersion` from the build inputs but the
local `Rebuild` you do in step 2 uses these defaults. Keep them honest.

> **Note:** In CI the `AddVersion` target in `OmenMon.csproj` rewrites this
> file before compile. The split is deliberate:
>
> - `AssemblyVersion` keeps only the first three segments (X.Y.Z, .NET pads
>   to X.Y.Z.0). Stable — changing it would break assembly binding.
> - `AssemblyFileVersion` gets the full four segments, where the 4th is the
>   `BUILD_NUMBER` repo variable (auto-incremented by `build_bump.yml` via
>   `VARIABLE_WRITE_TOKEN`).
>
> So a shipped binary's *file* version reads as `X.Y.Z.<build#>` (e.g.
> `1.5.0.42`) in Windows file properties / `Get-FileHash` output, while its
> *assembly* version stays at `X.Y.Z.0`. The first three segments are what
> you bump here; the fourth is just a CI counter on `AssemblyFileVersion`.

### `wiki/Home.md`

```markdown
Fork maintained by [@seakyy](...). Current release: **v1.5.0-reborn** (YYYY-MM-DD).
```

### `CHANGELOG.md`

Promote your `## [Unreleased]` section to a real version header:

```markdown
## [1.5.0-reborn] - 2026-MM-DD

> Short one-paragraph summary.

### Fixed
- ...
### Added
- ...
### Changed
- ...
### Removed
- ...
```

The `## [X.Y.Z-reborn]` header is what `draft-release.yml` greps for. **Match
the version exactly** — `1.5.0` and `1.5.0-reborn` are both accepted by the
extractor, but `v1.5.0` (with the `v` prefix) is not.

---

## Step 2 — Build locally

```bash
"/c/Program Files/Microsoft Visual Studio/2022/Enterprise/MSBuild/Current/Bin/MSBuild.exe" \
  -m -noLogo -restore -p:Configuration=Release -t:Rebuild OmenMon.sln
```

Two things to check in the output:

- `Build succeeded.` with **0 Warning(s), 0 Error(s)**. Don't ship a build
  with warnings; future-you won't be able to tell new warnings from old ones.
- The line `OmenMon -> ...\Bin\OmenMon.exe` actually appears. Confirms the
  embedded resource picked up `Resources/LpcACPIEC.bin` and the binary made
  it to `Bin/`.

If you see `error MSB3030` about a missing `Resources/LpcACPIEC.bin`, that's
not a bug in the build — it's the Release-time safety check refusing to
ship without the signed Pawn module. Fetch the latest blob from
[PawnIO.Modules releases](https://github.com/namazso/PawnIO.Modules/releases),
drop it into `Resources/`, and rebuild.

---

## Step 3 — Run the tests

```bash
"/c/Program Files/dotnet/dotnet" test Tests/OmenMon.Tests/OmenMon.Tests.csproj \
  -c Release --nologo --no-build
```

Expected output: **`Passed!  - Failed: 0, Passed: 17, Skipped: 0`** (or
higher as more model entries are added).

If any test fails, **don't tag**. The model database is what users on
unfamiliar hardware depend on; a broken entry causes nonsense readouts that
look like a crash to a non-technical user. Fix the offending `<Model>`
entry in `OmenMon.xml` first.

The test project is .NET 8 SDK-style; the main project is .NET Framework 4.8.
That mismatch is intentional and supported — `dotnet test` builds and runs
the test project independently, the .exe you ship is untouched.

---

## Step 4 — SHA256 checksum

**As of v1.4.1 the release CI generates `SHA256SUMS.txt` automatically**
when a GitHub release is published. The
`.github/workflows/release.yml` job runs `sha256sum` over the zipped
artifact (and the inner `OmenMon.exe` if present in the bundle) and
uploads the result as a release asset alongside the .zip. Users can
then verify with:

```powershell
# Windows / PowerShell
Get-FileHash -Algorithm SHA256 .\OmenMon-v1.4.1-reborn-Release.zip
```

```bash
# Linux / WSL / Git Bash
sha256sum -c SHA256SUMS.txt
```

> **A new build always produces a new hash.** The AssemblyFileVersion is
> stamped per build (BUILD_NUMBER comes from the workflow's environment),
> so even a no-op rebuild of the same commit will produce a different
> binary. Never reuse a SHA-256 across releases — the CI-generated
> `SHA256SUMS.txt` is the single source of truth for each release.

### Manual hash (only if publishing without CI)

If for some reason you publish the .zip directly without going through
the `release: published` workflow, compute the hash locally and paste
it under a `## SHA256 checksums` section in the release notes:

```bash
"/c/Program Files/Git/usr/bin/sha256sum.exe" OmenMon.zip
```

```powershell
Get-FileHash .\OmenMon.zip -Algorithm SHA256
```

Do this **after** the build and **before** uploading — anything you do to
the file between (re-signing, repacking) invalidates the digest.

---

## Step 5 — Commit and push the version bump

```bash
git add -A
git status                                    # sanity check the diff
git commit -m "Release v1.5.0"
git push origin master
```

Push to `master`, not the working branch — the tag in step 6 will be cut
from this commit. If you've been working on a `v1.5.0` branch the way you
did for v1.4.0, merge it into `master` first (or set the tag at the merge
commit when you push).

---

## Step 6 — Tag and trigger the draft

```bash
git tag v1.5.0
git push origin v1.5.0
```

What happens automatically:

1. `.github/workflows/draft-release.yml` fires on the `v*.*.*` tag push.
2. It `awk`-greps `CHANGELOG.md` for `## [1.5.0]` or `## [1.5.0-reborn]`
   and copies everything until the next `## ` header.
3. It calls `gh release create v1.5.0 --draft --notes-file ...` which
   creates an editable draft release with your CHANGELOG section as the body.
4. Open <https://github.com/seakyy/OmenMon-Reborn/releases>; the draft sits
   at the top with a "Draft" badge.

**If you tagged wrong**, fix it without deleting the tag — re-run the
workflow manually:

- Actions tab → "OmenMon Draft Release" → Run workflow → type `1.5.0`.
- The workflow is idempotent: it sees the existing draft and refreshes its
  body instead of creating a duplicate.

**If you need to nuke the tag entirely** (last resort, e.g. you tagged the
wrong commit):

```bash
git tag -d v1.5.0
git push origin :refs/tags/v1.5.0          # delete remote tag
# Then delete the GitHub draft manually on the Releases page.
```

---

## Step 7 — Edit the draft and publish

In the GitHub Releases UI:

1. **Verify the auto-extracted notes are correct.** The workflow grabs
   from the first `## [version]` line to the next `## ` line — if your
   CHANGELOG has unusual nesting, scan for missing chunks.
2. **Attach binaries.** Drag `Bin/OmenMon.exe` into the "Attach binaries"
   box at the bottom. Optionally also attach a `.zip` if you've prepared
   one (the `release.yml` workflow will build one after publish anyway, so
   this is purely so the download is available immediately).
3. **Paste the SHA256 line** at the bottom of the body.
4. **Don't check "Set as a pre-release"** unless this is genuinely a
   release candidate. End-users only see "Latest" downloads by default.
5. **Click "Publish release"**.

The moment you click Publish, `release.yml` fires. It:

- Re-runs the build with the published tag as `AssemblyVersion`.
- Zips the result with a versioned filename like `OmenMon-v1.5.0-Release.zip`.
- Uploads the zip as a release asset alongside your manually-attached .exe.

Refresh the release page after a minute or two — you should see the zip
appear next to the .exe.

---

## Step 8 — Post-release housekeeping

Things to do after publishing, in rough order of urgency:

1. **Pin the release to the top of the repo.** Releases page → click the
   "..." menu on your release → "Pin release". Especially important for
   security-flavoured releases (v1.4.0 was one).
2. **Reply on every issue the release closes** with a short note:
   > Fixed in v1.5.0 — please grab the latest build and reply if anything's
   > still off. Closing.
   Then close them. The `Closes #N` lines in the PR body auto-close on
   merge but a comment is friendlier than a silent close.
3. **For security releases especially: reply on the canonical "this bug is
   reported again" thread** (e.g. #1 for the Defender issue) so users
   landing there from Google immediately see the resolution.
4. **Bump `## [Unreleased]` back into `CHANGELOG.md`** at the top so the
   next development cycle has somewhere to write to:
   ```markdown
   ## [Unreleased]

   ## [1.5.0-reborn] - 2026-MM-DD
   ...
   ```
5. **Tweet / Discord-announce** if that's your style. Otherwise just
   trust the GitHub release feed.

---

## Module rotation (PawnIO `LpcACPIEC.bin`)

The PawnIO module ships as a signed binary that's redistributed unmodified
from upstream. When namazso publishes a new version, here's how to update
ours:

```bash
# 1. Grab the latest release zip from PawnIO.Modules
#    https://github.com/namazso/PawnIO.Modules/releases
#    Download release_X_Y_Z.zip and extract LpcACPIEC.bin from it.

# 2. Replace our embedded copy
cp /path/to/extracted/LpcACPIEC.bin Resources/LpcACPIEC.bin

# 3. Sanity-check the file is reasonably-sized (signed AMX blob)
ls -la Resources/LpcACPIEC.bin            # expect roughly 2-3 KB

# 4. Rebuild and run OmenMon locally
"/c/Program Files/Microsoft Visual Studio/2022/Enterprise/MSBuild/Current/Bin/MSBuild.exe" \
  -m -noLogo -restore -p:Configuration=Release -t:Rebuild OmenMon.csproj

# 5. Verify the new build loads it
./Bin/OmenMon.exe -Diag    # check the "Kernel Driver (PawnIO)" section shows IsOpen: True

# 6. Commit
git add Resources/LpcACPIEC.bin
git commit -m "Rotate LpcACPIEC.bin to release_X_Y_Z"
```

If a rotation breaks something (e.g. signature verification fails because
PawnIO updated their pinned keys but the user's installed driver is older),
the `OmenMon-crash-*.log` and `-Diag` reports will show a `pawnio_load
returned 0x...` line and your bug-report process is fast. See
`Resources/PAWN_BUILD.md` for the long-form rationale.

---

## Diagnostic toolkit (use this when triaging issues)

When a user reports a bug, ask them to run **one** of these:

- **`OmenMon.exe -Diag`** — writes `OmenMon-Diag-yyyy-mm-dd-HHmmss.md` next
  to the executable, contents are pasteable into a GitHub issue.
- **Tray menu → "Copy Diagnostic Info"** — same content, straight to the
  clipboard. Use for less technical users.

What you get back (and what to look for):

| Section | Look for |
|---|---|
| Environment | OmenMon version (are they actually on the latest?), OS bitness |
| Kernel Driver (PawnIO) | `IsOpen: True`? if False, PawnIO isn't installed correctly |
| Model Database Match | Native preset or "no native preset"? — that tells you whether they need a model entry added |
| Auto-Calibration Override | HasCpu / HasGpu flags + sidecar XML — confirms whether the wizard ran and what it found |
| Recent EC Activity | Ring buffer of EC ops, useful for intermittent bugs (#49-style spikes) |
| Crash Logs | List of `OmenMon-crash-*.log` files — **ask them to attach these too** |
| Hardware Probe | The classic `-Probe` dump, same content as before |

**Crash logs (`OmenMon-crash-*.log`) are not auto-attached** — the user
has to manually drag-drop them into the issue. Always ask, because they
contain the full exception + diag bundle in one file and that's typically
the smoking gun.

If the install directory is read-only (Program Files install without
elevation), crash logs land in
`%LOCALAPPDATA%\OmenMon-Reborn\` instead. Tell the user to check there.

---

## Things that go wrong (and what to do)

### Tag pushed but no draft appeared

- Check Actions tab → "OmenMon Draft Release" → did the workflow run?
- If it ran red: open the failed run, read the log. Most likely cause:
  the CHANGELOG section couldn't be found (version mismatch between tag
  and CHANGELOG header). Fix CHANGELOG, then re-run the workflow manually
  (Actions → Run workflow → type the version).
- If it didn't run at all: did you push the tag? `git push origin v1.5.0`
  is **not** the same as `git push origin master`. Tags need their own push.

### `MSB3030 — Missing required file: Resources/LpcACPIEC.bin`

Working as intended. Release builds refuse to ship without the signed
Pawn module. Pull the latest from PawnIO.Modules releases (see "Module
rotation" above), drop it into `Resources/`, rebuild.

### `pawnio_load returned 0x80070057` at runtime

Bad blob. The user's `LpcACPIEC.bin` (either side-by-side override or
embedded) is missing, corrupt, or signed with a key the installed
`PawnIO.sys` doesn't trust. Most often: their PawnIO install is older
than the module rotation. Tell them to update PawnIO from
<https://pawnio.eu/>.

### Defender re-flags the .exe

If users start reporting Defender warnings again after a v1.4.0+
release:

1. Confirm they're on the **new** version, not a stale v1.3.x. The
   informational version is shown in the tray's "About" dialog and at
   the top of `-Diag` output.
2. If they're on the new version and still flagged, it's likely an
   Anthropic / heuristic false positive on `OmenMon.exe` itself (the
   PawnIO driver is signed and not the cause). Submit the binary to
   <https://www.microsoft.com/wdsi/filesubmission> as a false-positive
   report. They typically respond within 48 h.

### Tests fail in CI but pass locally (or vice versa)

`Tests/OmenMon.Tests` walks parent directories to find `OmenMon.xml`. If
CI runs from a different working directory than locally, the test walk
might miss the XML. Reproduce by deleting `Bin/OmenMon.xml` locally and
running tests again; if that breaks them, that's the cause.

---

## Numbering convention

`MAJOR.MINOR.PATCH-reborn`:

- **MAJOR** — kernel-driver-class or fork-defining change. PawnIO migration
  was 1.4.0 because it changed the install model entirely.
- **MINOR** — new user-visible features or non-trivial fixes. Hysteresis,
  per-app fan profiles, new BIOS surfaces. Typical cadence: every few weeks.
- **PATCH** — pure fix release with no new functionality. Cut ad-hoc when
  a regression is bad enough to warrant it; don't wait for the next minor.

The `-reborn` suffix stays for the lifetime of the fork. Don't drop it
just because the version got bigger.

---

## Checklist (rip this out and tape it next to your monitor)

```
[ ] Version bumped in All/Version.cs, wiki/Home.md, CHANGELOG.md
[ ] `## [Unreleased]` section promoted to a real version header in CHANGELOG
[ ] Build clean: 0 warnings, 0 errors, Release config
[ ] Tests pass: 17/17 in OmenMon.Tests
[ ] SHA256 computed and copied
[ ] Master commit pushed
[ ] Tag pushed: git tag vX.Y.Z && git push origin vX.Y.Z
[ ] Draft release created (verify in GitHub UI)
[ ] Notes look right
[ ] OmenMon.exe attached
[ ] SHA256 pasted at bottom of notes
[ ] Published
[ ] release.yml ran and uploaded the zip
[ ] Closed issues commented on and closed
[ ] Pinned (for security/major releases)
[ ] Re-added `## [Unreleased]` to CHANGELOG for the next cycle
```
