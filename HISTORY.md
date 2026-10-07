# 历史源码的来源

当前程序版本为 **1.9.1**，完整当前源码位于仓库根目录的 `src`，测试与构建说明见 [README](README.md#开发与构建)。`history` 保留以下三个真实旧版快照，供查看对应版本的实现；它们不是原始 Git 提交历史，也不能作为当前二进制行为的证据。

本次在 2026-10-07 从作者本机保留的源码 ZIP 导入，压缩包 CRC 通过。导入时只移除统一外层 `Gameorder/` 目录（如存在）和排除 `bin/obj` 构建目录，源代码、版本字段与内容未修改。每个导入文件的 SHA256 记录在 [PUBLIC-SNAPSHOT-MANIFEST.json](PUBLIC-SNAPSHOT-MANIFEST.json)。

| 版本 | 原始源码包 | 源码目录 | 原包 SHA256 |
| --- | --- | --- | --- |
| 1.8.0 | `Gameorder-1.8.0-source.zip` | [history/v1.8.0](history/v1.8.0) | `ED037C8FFA14A62721CCF98F897E616B6ECFE12D9A403DE172DFECDA9D3DA4BB` |
| 1.8.3 | `Gameorder-1.8.3-source.zip` | [history/v1.8.3](history/v1.8.3) | `58A47A6687345EE869018E9FBFC2FB9D99168060E69F34AAADF7495D4F998FFB` |
| 1.8.5 | `Gameorder-1.8.5-source.zip` | [history/v1.8.5](history/v1.8.5) | `F78999DF520F6183F993EA999BD87052202E2E4A493B1CE8E0F739CB8826EBB4` |

旧版内部 README、更新日志与上传说明保持导入内容；其中版本号、界面、用法、发布范围和「未开源／未上传」等文字描述当时的状态，不能用于判断当前仓库。当前说明以仓库根 [README](README.md)、[SECURITY](SECURITY.md)、[CHANGELOG](CHANGELOG.md) 和 [LICENSE](LICENSE) 为准。

原历史包没有附带许可证；作者本次将仓库中的当前源码与这些历史快照按 **GNU GPL v3.0 only（GPL-3.0-only）** 发布。Copyright © 2026 XIAOYING223，完整条款见根目录 [LICENSE](LICENSE)。

更新日志中的早期开发阶段不是独立源码 Release，不编造不存在的 0.1 等源码版本。以上三个快照可按各自说明构建，但不能用于构建当前 1.9.1。
