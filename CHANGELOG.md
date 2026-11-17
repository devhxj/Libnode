# Changelog

## 2026-11-17

### 未发布 | Unreleased

中文 | English

#### 新增功能

- 提供 Node.js 26.13.1 LTS 的 Linux x64 原生构建，与 Microsoft.JavaScript.NodeApi 0.9.36 兼容，并复用原生与托管测试验证。by @devhxj

#### 问题修复

- 修复 Linux 进程内加载时原生 addon 无法解析 Node-API 符号的问题，并验证 Rolldown 的原生 minify 调用。by @devhxj

#### 其他变更

- 从语义版本标签统一确定包版本，通过候选验证后使用 GitHub Actions trusted publishing 发布 NuGet、GitHub Packages 和 GitHub Release。by @devhxj

New Features

- Build Node.js 26.13.1 LTS for Linux x64, compatible with Microsoft.JavaScript.NodeApi 0.9.36 and verified with reused native and managed tests. by @devhxj

Bug Fixes

- Fix unresolved Node-API symbols when native addons load inside a Linux .NET process, including a verified Rolldown native minify call. by @devhxj

Chores

- Derive one package version from a semantic version tag and publish verified packages to NuGet, GitHub Packages and GitHub Releases through GitHub Actions trusted publishing. by @devhxj
