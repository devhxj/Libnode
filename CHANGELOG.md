# Changelog

## 2026-11-17

### Libnode 0.2.0

中文 | English

#### 新增功能

- 增加 Linux ARM64 和 Windows ARM64 的原生构建、架构检查及独立运行验证流程，发布时要求全部平台通过验证。by @devhxj
- 增加 30 项 Node 26 运行时特性验证，覆盖 Temporal、Web API、加密、Worker、WebAssembly、模块、TypeScript 和 SQLite，并作为包发布门禁。by @devhxj

#### 其他变更

- 支持 ARM64 构建产物复用，并记录 32 位 Linux 和 Windows 的上游及 .NET 平台限制。by @devhxj

New Features

- Add native Linux ARM64 and Windows ARM64 builds, binary architecture checks and native runner verification, with acceptance required on every release platform. by @devhxj
- Add 30 Node 26 runtime checks covering Temporal, Web APIs, crypto, workers, WebAssembly, modules, TypeScript and SQLite as package publishing gates. by @devhxj

Chores

- Reuse ARM64 native build outputs and document upstream and .NET constraints for 32-bit Linux and Windows. by @devhxj

### Libnode 0.1.0

中文 | English

#### 新增功能

- 提供 Node.js 26.13.1 LTS 的 Linux x64 原生构建，与 Microsoft.JavaScript.NodeApi 0.9.36 兼容，并复用原生与托管测试验证。by @devhxj

#### 问题修复

- 修复 Linux 进程内加载时原生 addon 无法解析 Node-API 符号的问题，并验证 Rolldown 的原生 minify 调用。by @devhxj
- 修复 Windows 构建中的指针移动、快照 API 导出和 Rust 同步库链接问题，并通过原生、托管和 NuGet 消费端验证。by @devhxj

#### 其他变更

- 从语义版本标签统一确定包版本，通过候选验证后使用 GitHub Actions trusted publishing 发布 NuGet、GitHub Packages 和 GitHub Release。by @devhxj
- 支持上传本地已验证的原生构建，在 Actions 中复验和发布，避免重复编译 V8。by @devhxj
- 为 0.1.0 发布闭环加入 Windows x64 原生包，并在 Windows runner 上复验。by @devhxj

New Features

- Build Node.js 26.13.1 LTS for Linux x64, compatible with Microsoft.JavaScript.NodeApi 0.9.36 and verified with reused native and managed tests. by @devhxj

Bug Fixes

- Fix unresolved Node-API symbols when native addons load inside a Linux .NET process, including a verified Rolldown native minify call. by @devhxj
- Fix Windows pointer moves, snapshot API exports and Rust synchronization linking, verified with native, managed and actual NuGet consumer tests. by @devhxj

Chores

- Derive one package version from a semantic version tag and publish verified packages to NuGet, GitHub Packages and GitHub Releases through GitHub Actions trusted publishing. by @devhxj
- Upload a verified local native build for fresh verification and publishing in Actions without recompiling V8. by @devhxj
- Add the Windows x64 runtime package and verify it on the Windows runner for the 0.1.0 release closure. by @devhxj
