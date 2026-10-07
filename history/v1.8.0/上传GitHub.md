# 上传 Gameorder 到 GitHub

当前仅准备本地源码与发布资料，未创建 GitHub 仓库或上传文件。作者署名为 XIAOYING223；这不是预设的 GitHub 账号名称。

## 准备源码

将干净的源码包解压到新的工作目录。确认包含 `src`、`tests`、`Build.ps1`、`README.md`、`CHANGELOG.md`、`.gitignore` 与 `.gitattributes`。必须保留 `src/assets` 中的应用图标和作者头像。

构建生成的 `bin`、`obj`、`dist`，旧版程序、验证截图、宣传视频和个人账号资料不属于源码上传内容。`.gitignore` 会在 Git 添加文件时排除这些内容；网页上传时，请直接使用已清理的源码包内容。

## 创建并上传

1. 登录自己的 GitHub，打开 [创建仓库页面](https://github.com/new)。名称可使用 `Gameorder`，简介可填写「Steam 多账号管理与任务栏快捷切换工具」。自行选择所属账号与公开／私有状态。导入已有项目时不要重复生成 README、忽略文件或许可证。[GitHub 创建仓库说明](https://docs.github.com/en/repositories/creating-and-managing-repositories/creating-a-new-repository)
2. 在新仓库使用 **Upload files**，将解压后的源码文件和文件夹上传，并填写提交说明。上传源码内容，让仓库根目录直接显示 README 和 src；不要只上传一个源码 ZIP。[GitHub 上传文件说明](https://docs.github.com/en/repositories/working-with-files/managing-files/adding-a-file-to-a-repository)
3. 页面上传不便保留隐藏文件时，可使用 GitHub Desktop 或 Git 导入整个干净源码目录；使用自己已配置的 Git 身份。[GitHub 本地项目导入说明](https://docs.github.com/en/migrations/importing-source-code/using-the-command-line-to-import-source-code/adding-locally-hosted-code-to-github)

当前未选择开源许可证。发布许可由作者确定；源码可见与授予修改／再发布许可是不同的决定。

## 发布可运行版本

先运行 `Build.ps1 -SelfContained`，得到 Windows x64 单文件程序。将发布程序与使用说明整理为单独的 Release 压缩包，不包含本机账号资料。

在仓库的 Releases 页面创建草稿，版本标签使用 `v1.8.0`，标题使用 `Gameorder 1.8.0`，文案参考 [发布模板](docs/RELEASE_TEMPLATE.md)。上传可运行程序包作为附件，完成检查后再发布。发布包不需进入源码提交。[GitHub Release 说明](https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository)

上传完成后，可把实际仓库地址补充到 README；此前不填写猜测的仓库或个人主页地址。
