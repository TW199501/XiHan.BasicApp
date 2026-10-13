// 在临时 git 仓里实际跑 install-git-hooks.mjs，检查钩子写到了哪里。
import { execFileSync } from 'node:child_process'
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

// vitest 以 frontend 为工作目录；jsdom 环境下 import.meta.url 不是 file: 地址
const SCRIPT = resolve(process.cwd(), 'scripts', 'install-git-hooks.mjs')
const SHARED_HOOK = '#!/bin/sh\necho shared\n'

let tmp
let env

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

function makeSharedHooks() {
  const shared = join(tmp, 'shared-hooks')
  mkdirSync(shared)
  writeFileSync(join(shared, 'pre-commit'), SHARED_HOOK)
  return shared
}

function runInstaller(repo) {
  execFileSync('node', [join(repo, 'frontend', 'scripts', 'install-git-hooks.mjs')], {
    cwd: join(repo, 'frontend'),
    env,
    stdio: 'pipe',
  })
}

describe('install-git-hooks', () => {
  beforeEach(() => {
    tmp = mkdtempSync(join(tmpdir(), 'install-git-hooks-'))
    // 与真实的全局配置隔离
    const globalConfig = join(tmp, 'gitconfig')
    writeFileSync(globalConfig, '')
    env = { ...process.env, GIT_CONFIG_GLOBAL: globalConfig, GIT_CONFIG_NOSYSTEM: '1' }
  })

  afterEach(() => rmSync(tmp, { recursive: true, force: true }))

  it('没有设定 core.hooksPath 时写进本仓 .git/hooks', () => {
    const repo = makeRepo()
    runInstaller(repo)
    expect(existsSync(join(repo, '.git', 'hooks', 'pre-commit'))).toBe(true)
  })

  it('仓内设定的 core.hooksPath 照旧写进那个目录', () => {
    const repo = makeRepo()
    git(repo, 'config', 'core.hooksPath', '.githooks')
    runInstaller(repo)
    expect(existsSync(join(repo, '.githooks', 'pre-commit'))).toBe(true)
  })

  it('仓内名字以 .. 开头的目录仍算仓内', () => {
    const repo = makeRepo()
    git(repo, 'config', 'core.hooksPath', '..hooks')
    runInstaller(repo)
    expect(existsSync(join(repo, '..hooks', 'pre-commit'))).toBe(true)
  })

  it('全局 core.hooksPath 指向仓外时，不改共享目录，改写本仓 .git/hooks', () => {
    const shared = makeSharedHooks()
    const repo = makeRepo()
    git(repo, 'config', '--global', 'core.hooksPath', shared)
    runInstaller(repo)
    expect(readFileSync(join(shared, 'pre-commit'), 'utf8')).toBe(SHARED_HOOK)
    expect(existsSync(join(repo, '.git', 'hooks', 'pre-commit'))).toBe(true)
  })

  it('仓内的 hooks 目录是指向仓外的符号链接时，同样不改共享目录', () => {
    const shared = makeSharedHooks()
    const repo = makeRepo()
    symlinkSync(shared, join(repo, '.githooks'), 'dir')
    git(repo, 'config', 'core.hooksPath', '.githooks')
    runInstaller(repo)
    expect(readFileSync(join(shared, 'pre-commit'), 'utf8')).toBe(SHARED_HOOK)
    expect(existsSync(join(repo, '.git', 'hooks', 'pre-commit'))).toBe(true)
  })
})
