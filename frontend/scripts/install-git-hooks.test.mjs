// node --test scripts/install-git-hooks.test.mjs
// 在临时 git 仓里实际跑 install-git-hooks.mjs，检查钩子写到了哪里。
import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, test } from 'node:test'
import { fileURLToPath } from 'node:url'

const SCRIPT = fileURLToPath(new URL('install-git-hooks.mjs', import.meta.url))

let tmp
let env

beforeEach(() => {
  tmp = mkdtempSync(join(tmpdir(), 'install-git-hooks-'))
  // 与真实的全局配置隔离
  const globalConfig = join(tmp, 'gitconfig')
  writeFileSync(globalConfig, '')
  env = { ...process.env, GIT_CONFIG_GLOBAL: globalConfig, GIT_CONFIG_NOSYSTEM: '1' }
})

afterEach(() => rmSync(tmp, { recursive: true, force: true }))

function git(cwd, ...args) {
  return execFileSync('git', args, { cwd, env, encoding: 'utf8' }).trim()
}

/** 建一个带 frontend/package.json（含 gitHooks）的仓，返回仓根目录 */
function makeRepo() {
  const repo = join(tmp, 'repo')
  mkdirSync(join(repo, 'frontend', 'scripts'), { recursive: true })
  git(tmp, 'init', '-q', repo)
  writeFileSync(
    join(repo, 'frontend', 'package.json'),
    JSON.stringify({ gitHooks: { 'pre-commit': 'npx --no -- lint-staged' } }),
  )
  copyFileSync(SCRIPT, join(repo, 'frontend', 'scripts', 'install-git-hooks.mjs'))
  return repo
}

function runInstaller(repo) {
  execFileSync('node', [join(repo, 'frontend', 'scripts', 'install-git-hooks.mjs')], {
    cwd: join(repo, 'frontend'),
    env,
    stdio: 'pipe',
  })
}

test('没有设定 core.hooksPath 时写进本仓 .git/hooks', () => {
  const repo = makeRepo()
  runInstaller(repo)
  assert.ok(existsSync(join(repo, '.git', 'hooks', 'pre-commit')))
})

test('仓内设定的 core.hooksPath 照旧写进那个目录', () => {
  const repo = makeRepo()
  git(repo, 'config', 'core.hooksPath', '.githooks')
  runInstaller(repo)
  assert.ok(existsSync(join(repo, '.githooks', 'pre-commit')))
})

test('全局 core.hooksPath 指向仓外时，不改共享目录，改写本仓 .git/hooks', () => {
  const shared = join(tmp, 'shared-hooks')
  mkdirSync(shared)
  writeFileSync(join(shared, 'pre-commit'), '#!/bin/sh\necho shared\n')
  const repo = makeRepo()
  git(repo, 'config', '--global', 'core.hooksPath', shared)
  runInstaller(repo)
  assert.equal(
    execFileSync('cat', [join(shared, 'pre-commit')], { encoding: 'utf8' }),
    '#!/bin/sh\necho shared\n',
  )
  assert.ok(existsSync(join(repo, '.git', 'hooks', 'pre-commit')))
})
