const { execFileSync } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');

const tagPattern = /^v[0-9][A-Za-z0-9.+_-]*$/;

function readNotes(file) {
  const notes = fs.readFileSync(file, 'utf8').trim();
  if (!notes) throw new Error(`Release notes are empty: ${file}`);
  return `${notes}\n`;
}

function generateNotes(tag, cwd = process.cwd(), repository = 'MaTriXovskyy/Lupik') {
  if (!tagPattern.test(tag)) throw new Error(`Invalid release tag: ${tag}`);
  const git = (...args) => execFileSync('git', args, {
    cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'],
  }).trim();
  // Validate the ref even when a curated file exists.
  git('rev-parse', '--verify', `${tag}^{commit}`);
  const curated = path.join(cwd, 'release-notes', `${tag}.md`);
  if (fs.existsSync(curated)) return readNotes(curated);

  let previous;
  try {
    previous = git('describe', '--tags', '--abbrev=0', '--match', 'v[0-9]*', `${tag}^`);
  } catch {
    // The first version has no previous reachable version tag.
  }
  const range = previous ? `${previous}..${tag}` : tag;
  const commits = git('log', '--reverse', '--format=%H', range).split(/\r?\n/).filter(Boolean);
  if (!commits.length) throw new Error(`No commits found for ${tag}`);

  const url = `https://github.com/${repository}`;
  const sections = commits.map(sha => {
    const message = git('show', '-s', '--format=%B', sha);
    const [subject, ...body] = message.split(/\r?\n/);
    return `### ${subject}\n\n${body.join('\n').trim()}\n\n[Commit ${sha.slice(0, 7)}](${url}/commit/${sha})`.replace(/\n{3,}/g, '\n\n');
  });
  const changelog = previous
    ? `\n\n**Full changelog:** [${previous}...${tag}](${url}/compare/${previous}...${tag})`
    : '';
  return `## Changes\n\n${sections.join('\n\n')}${changelog}\n`;
}

if (require.main === module) {
  const [tag, output] = process.argv.slice(2);
  if (!output) throw new Error('Usage: node tools/release-notes.cjs <tag> <output.md>');
  fs.writeFileSync(output, generateNotes(tag, process.cwd(), process.env.GITHUB_REPOSITORY), 'utf8');
}

module.exports = { generateNotes, readNotes, tagPattern };
