const fs = require('node:fs');
const path = require('node:path');
const { readNotes, tagPattern } = require('./release-notes.cjs');

// Backfill only empty descriptions; preserve release metadata, assets and authored notes.
async function syncReleaseNotes({ github, context, core, cwd = process.cwd() }) {
  const directory = path.join(cwd, 'release-notes');
  for (const file of fs.readdirSync(directory).sort()) {
    if (!file.endsWith('.md') || !tagPattern.test(file.slice(0, -3))) continue;
    const tag = file.slice(0, -3);
    const body = readNotes(path.join(directory, file));
    let release;
    try {
      ({ data: release } = await github.rest.repos.getReleaseByTag({ ...context.repo, tag }));
    } catch (error) {
      if (error.status !== 404) throw error;
      core.info(`${tag}: not published yet; notes will be used by the release build.`);
      continue;
    }
    if (release.body?.trim()) {
      core.info(`${tag}: existing description preserved.`);
      continue;
    }
    await github.rest.repos.updateRelease({ ...context.repo, release_id: release.id, body });
    const { data: updated } = await github.rest.repos.getReleaseByTag({ ...context.repo, tag });
    if (updated.body?.trim() !== body.trim()) throw new Error(`${tag}: release notes verification failed.`);
    core.info(`${tag}: release notes updated and verified.`);
  }
}

module.exports = { syncReleaseNotes };
