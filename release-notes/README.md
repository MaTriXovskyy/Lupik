# Release notes

The tag release workflow uses `release-notes/<tag>.md` when present. Otherwise,
`tools/release-notes.cjs` includes the full commit messages since the previous
reachable version tag (or the full history for the first version). Checkout must
fetch the full history and tags. Empty curated notes stop the build.

The generated Markdown is passed to `vpk pack --releaseNotes`. Velopack 1.2.161
reads the package's release notes when `vpk upload github` creates the release,
so both the update package and the GitHub release receive the description.

Pushing curated notes to `main` also runs **Backfill release notes**. It fills
only empty descriptions of existing releases, checks the saved text, and leaves
release metadata and assets untouched. Missing releases and nonempty descriptions
are skipped. The workflow can also be run manually.

Run the release notes checks with:

```sh
node --test tools/release-notes.test.cjs
```
