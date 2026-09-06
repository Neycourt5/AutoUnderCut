# Repository working instructions

The user wants completed plugin updates published to GitHub so they can update
through the in-game Dalamud installer. This is standing authorization to commit,
push, and publish releases for requested changes in this repository.

- After implementing and validating a plugin update, increment the version in
  `src/SmartUndercutBot/SmartUndercutBot.csproj` to an unused higher version.
- Commit the intended changes, push to `origin/main`, and push the matching
  annotated `v<version>` tag to trigger `.github/workflows/release.yml`.
- Wait for the release workflow, resolve failures, and verify the published
  `repo.json` version and `SmartUndercutBot.zip` before calling the update ready.
- Preserve the installer URL:
  `https://github.com/Neycourt5/AutoUnderCut/releases/latest/download/repo.json`.
- Keep `PLAN.md` current so interrupted work can resume. Report publication
  blockers explicitly; do not describe a source-only push as an in-game update.
- Do not force-push, replace published tags, or publish failing builds. Do not
  claim native game operations were tested unless they were actually exercised.
