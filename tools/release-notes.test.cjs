const { test } = require('node:test');
const assert = require('node:assert/strict');
const { execFileSync } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { generateNotes } = require('./release-notes.cjs');
const { syncReleaseNotes } = require('./sync-release-notes.cjs');

function fixture(t) {
  const cwd = fs.mkdtempSync(path.join(os.tmpdir(), 'lupik-notes-'));
  t.after(() => fs.rmSync(cwd, { recursive: true, force: true }));
  const git = (...args) => execFileSync('git', args, { cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
  git('init', '-q');
  git('config', 'user.name', 'Release notes test');
  git('config', 'user.email', 'release-notes@example.invalid');
  const commit = message => git('commit', '--allow-empty', '-qm', message);
  const notes = (tag, body) => {
    fs.mkdirSync(path.join(cwd, 'release-notes'), { recursive: true });
    fs.writeFileSync(path.join(cwd, 'release-notes', `${tag}.md`), body);
  };
  return { cwd, git, commit, notes };
}

test('future tags retain full commit details and exclude previous versions', t => {
  const f = fixture(t);
  f.commit('Previous version\n\nOld feature');
  f.git('tag', 'v1.6.0');
  f.commit('New feature\n\n- Detailed behaviour\n- Zażółć gęślą jaźń');
  f.commit('Fix\n\nKeyboard behaviour');
  f.git('tag', '-a', 'v1.7.0', '-m', 'Annotated release tag');
  f.commit('Unreleased work');
  const body = generateNotes('v1.7.0', f.cwd);
  assert.match(body, /Detailed behaviour/);
  assert.match(body, /Zażółć gęślą jaźń/);
  assert.match(body, /Keyboard behaviour/);
  assert.match(body, /v1\.6\.0\.\.\.v1\.7\.0/);
  assert.doesNotMatch(body, /Old feature|Unreleased work/);
  assert.ok(body.indexOf('New feature') < body.indexOf('### Fix'));
});

test('first tag includes history and supports a commit without a body', t => {
  const f = fixture(t);
  f.commit('First feature');
  f.git('tag', 'v1.0.0');
  assert.match(generateNotes('v1.0.0', f.cwd), /### First feature/);
});

test('curated notes win and normalize whitespace at the boundary', t => {
  const f = fixture(t);
  f.commit('Automatic notes');
  f.git('tag', 'v1.6.0');
  f.notes('v1.6.0', '\n## QR and fonts\n\nCurated content\n');
  assert.equal(generateNotes('v1.6.0', f.cwd), '## QR and fonts\n\nCurated content\n');
});

test('empty curated notes, absent refs and unsafe tags fail', t => {
  const f = fixture(t);
  f.commit('Release');
  f.git('tag', 'v1.6.0');
  f.notes('v1.6.0', ' \n');
  assert.throws(() => generateNotes('v1.6.0', f.cwd), /empty/);
  assert.throws(() => generateNotes('v9.0.0', f.cwd));
  assert.throws(() => generateNotes('../outside', f.cwd), /Invalid release tag/);
  assert.throws(() => generateNotes('--help', f.cwd), /Invalid release tag/);
});

function api(t, initialBody, options = {}) {
  const f = fixture(t);
  f.notes('v1.6.0', '## QR and fonts\n');
  f.notes('README', 'Documentation');
  let body = initialBody;
  const writes = [];
  const args = {
    cwd: f.cwd,
    context: { repo: { owner: 'MaTriXovskyy', repo: 'Lupik' } },
    core: { info() {} },
    github: { rest: { repos: {
      async getReleaseByTag(request) {
        assert.equal(request.tag, 'v1.6.0');
        if (options.error) throw Object.assign(new Error('API error'), { status: options.error });
        return { data: { id: 401338563, body } };
      },
      async updateRelease(request) {
        writes.push(request);
        if (!options.badReadback) body = request.body;
      },
    } } },
  };
  return { args, writes };
}

test('backfill patches only the body and verifies it for null and whitespace', async t => {
  for (const body of [null, '', ' \n']) {
    const a = api(t, body);
    await syncReleaseNotes(a.args);
    assert.deepEqual(a.writes, [{ owner: 'MaTriXovskyy', repo: 'Lupik', release_id: 401338563, body: '## QR and fonts\n' }]);
  }
});

test('backfill preserves existing descriptions', async t => {
  const a = api(t, 'Already authored notes');
  await syncReleaseNotes(a.args);
  assert.equal(a.writes.length, 0);
});

test('backfill skips unpublished tags but fails on permission errors', async t => {
  const missing = api(t, null, { error: 404 });
  await syncReleaseNotes(missing.args);
  assert.equal(missing.writes.length, 0);
  const forbidden = api(t, null, { error: 403 });
  await assert.rejects(syncReleaseNotes(forbidden.args), /API error/);
});

test('backfill fails if the saved description does not match', async t => {
  const a = api(t, null, { badReadback: true });
  await assert.rejects(syncReleaseNotes(a.args), /verification failed/);
});
