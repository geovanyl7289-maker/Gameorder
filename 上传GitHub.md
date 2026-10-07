# 维护与发布

项目仓库：[geovanyl7289-maker/Gameorder](https://github.com/geovanyl7289-maker/Gameorder)。用户下载入口：[最新版 Release](https://github.com/geovanyl7289-maker/Gameorder/releases/latest)。当前版本为 **1.9.1**，完整应用源码、测试与构建脚本在仓库根目录，真实旧版源码快照位于 `history`。

Copyright © 2026 XIAOYING223。本项目使用 **GNU GPL v3.0 only（GPL-3.0-only）**；维护发布时保留 [LICENSE](LICENSE)、作者署名与对应版本源码。

## 构建与验证

在 Windows 上安装 .NET 9 SDK 与 Windows Desktop 开发支持，然后从干净的仓库工作区执行：

```powershell
dotnet build .\src\Gameorder.csproj -c Release -p:ResetTestBuild=false
dotnet run --project .\tests\Gameorder.Tests.csproj -c Release
.\Build.ps1 -SelfContained
```

正式输出位于 `dist`，为包含 .NET 运行时的 Windows x64 单文件程序。不要使用 `ResetTestBuild=true` 构建正式包。项目未引用第三方 NuGet 包；首次构建仍可能需要恢复 Microsoft 的框架与运行时包。

完整安全测试还包括实际 Windows DPAPI、密码加密、迁移、密码提示与隔离清空验证：

```powershell
dotnet run --project .\tests\security-storage\SecurityStorage.csproj -c Release
```

清空测试的显式开发数据目录受源码限制，必须位于 **`H:\Documents\Playground\` 下**。本地运行完整安全测试时，仓库与构建输出须在该路径内；CI 使用 `subst` 映射 H 盘后执行。克隆到 C 盘等位置仍可构建正式应用与运行核心测试，但部分清空验证会被路径保护拒绝。不要通过修改生产清空范围来让测试通过。

发布后的界面验证使用合成演示资料，不导入真实 Steam 账号：

```powershell
.\dist\Gameorder.exe --ui-smoke .\verification\manager
.\dist\Gameorder.exe --quick-ui-smoke .\verification\quick
```

验证需要可运行 WPF 的 Windows 环境。报告和 PNG 写入 `verification`，不作为个人数据上传；公开截图只使用明确标记的合成演示账号。实际 Steam 在线／离线认证须由用户在官方客户端完成。

## 发布 Release

1. 检查当前版本字段、根目录更新日志、使用和安全说明一致，确认仓库没有个人资料或构建产物。
2. 完成构建与相关验证，将该版本源码提交并建立对应版本标签，例如 `v1.9.1`。历史快照目录保留原始内容，不伪造旧版 Git 提交日期或标签。
3. 整理 Windows 程序包，例如 `Gameorder-1.9.1-win-x64.zip`，包含正式程序、许可证与需要的使用说明；计算最终 ZIP 的 SHA-256 并写入 `SHA256SUMS.txt`。
4. 在对应标签创建 Release，说明功能更新、兼容变化和下载用法；上传正式 ZIP 与校验文件，并提供该版本源码入口。
5. 检查 Release 的资产可下载，确认下载文件的 SHA-256 与发布值一致，再使用真实仓库或 Release 链接对外分享。

GitHub 自动生成的 **Source code** ZIP／tar.gz 包含该标签对应的完整当前源码，需要按 README 构建；Windows 用户应下载正式 `win-x64.zip`。独立重置测试程序按需制作，不自动附在正式发布包中。

仓库只上传经检查的源码、脚本、文档和演示资源，不直接上传整个安装目录。应用密码、账号库、锁配置、Steam 登录缓存、配置副本与切换备份均不属于发布材料。SHA-256 检查文件一致性；开源许可和校验值都不代替独立安全审计或代码签名。
