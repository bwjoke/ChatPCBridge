# ChatPCBridge

把微信主动导出的聊天 ZIP，带到 Windows 上的 ChatGPT。

[English](README.en.md) · [架构](docs/architecture.md) · [隐私与数据](docs/privacy.md) · [安全报告](SECURITY.md)

[![CI](https://github.com/bwjoke/ChatPCBridge/actions/workflows/ci.yml/badge.svg)](https://github.com/bwjoke/ChatPCBridge/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

ChatPCBridge 注册为 Windows 系统分享目标，接收你在微信中选择的聊天归档，先完整保存原件，再复制文件并打开 ChatGPT。它不读取微信数据库，不解密聊天记录，不注入或修改微信，也不需要 API key。

```text
微信多选 → 合并转发 → 转发到其他应用 → ChatPCBridge
                                      ↓
                             本地保留 ZIP + 备用 TXT
                                      ↓
                             打开 ChatGPT → Ctrl+V
```

这是独立的 Windows 实现，设计受到 [WeChatBridge](https://github.com/freestylefly/WeChatBridge) 启发。项目与腾讯、微信及 OpenAI 无隶属关系。

## 当前状态

当前源码版本为 **0.2.1**。早期本机原型已验证真实微信分享、ChatGPT 文件粘贴，以及连续分享复用同一窗口。公开源码还通过本机临时签名测试包验证了合成 ZIP 的首次与连续 Windows 原生分享；其他电脑的全新安装、不同系统与应用组合仍需实测。

- 首发提供源码和 CI 构建验证，**尚无面向公众的受信签名安装包**。
- CI 的 unsigned MSIX 用于构建验证，不能直接作为可信安装包安装。不要关闭 Windows 安全校验，也不要信任来源不明的证书。
- 微信入口实测环境为 Windows 11 25H2（26200 系列）和微信 4.1.15。菜单是否可用取决于系统与微信版本；不承诺所有 Windows 10/11 组合可用。
- 应用界面目前为中文；支持 x64 构建。其他环境需要进一步验证。

## 使用

1. 安装自己构建并签名的 MSIX 后，重新打开微信的合并转发菜单，选择 **ChatPCBridge**。
2. 等待原件保存完成。默认会复制文件并打开本机 ChatGPT。
3. 在目标对话输入框按 **Ctrl+V**；也可点击“打开文件夹”，拖入 ZIP 或通过 ChatGPT 的附件按钮选择文件。
4. 等附件出现，再输入分析要求并发送。应用不会替你发送消息。

ZIP 无法上传时，可点击“复制聊天 TXT”上传提取的文字。TXT 不包含图片、语音等附件的全部信息，原 ZIP 会保留。

“复制分析提示词”会替换剪贴板中的文件，建议先添加附件再复制提示词。目标可选新版 ChatGPT、ChatGPT Classic 或网页版，能否接收 ZIP 取决于目标应用和账户的实际能力。

应用也支持导入或拖入 ZIP/TXT。无需提前启动主窗口：Windows 分享会启动接收器；已有窗口会被复用。接收时可能短暂出现后台进程，文件保存完成后它会退出。关闭主窗口后，下次分享仍可重新启动。

程序文件名是 `ChatPCBridge.exe`，开始菜单、窗口标题与分享入口都叫 **ChatPCBridge**。应用自身的界面、导入框和提示词使用中性聊天归档文案；原始文件名与聊天正文按原样保留。文档中的兼容性说明和来源致谢保留相关产品的真实名称。

分享入口由 **MSIX 安装注册**，不会因为程序退出或分享完成而自动消失。只有构建或直接运行 EXE，不会注册分享入口。安装后若看不到，先关闭并重新打开来源应用的转发窗口，再确认系统“已安装的应用”中仍有 ChatPCBridge；不同来源应用、文件类型与系统版本也会影响列表。

## 数据保存在什么地方？

```text
%USERPROFILE%\ChatPCBridge\
  Inbox\<批次编号>\     原件、备用文本与 batch.json
  settings.json         目标应用与自动打开设置
  bridge.log            本地技术日志
```

原件位于安装包之外，更新或卸载不会主动删除归档。当前没有自动清理策略。数据没有额外加密；不要把整个收件目录、日志或 `batch.json` 上传到公开 Issue。详见[隐私说明](docs/privacy.md)。

早期私用原型使用独立的数据目录；公开版不自动移动或删除旧数据。需要迁移时，可通过“导入 ZIP / TXT”选择旧原件，旧应用仍可独立使用。

## 从源码构建

需要 Windows x64、PowerShell 7 和 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。SDK 版本由 `global.json` 指定。

```powershell
git clone https://github.com/bwjoke/ChatPCBridge.git
cd ChatPCBridge
pwsh -File ./scripts/build.ps1 -DownloadBuildTools
```

脚本从固定版本下载 Microsoft Windows SDK 打包工具到仓库内 `.tools`，验证工具的 Microsoft 签名，生成图标并构建包含 .NET 运行时的 MSIX。构建结果位于 `artifacts`；运行已安装的应用不需要 .NET SDK 或 Visual Studio。

本地签名、证书信任与卸载流程见[打包说明](packaging/README.md)。签名与信任是明确分开的操作，普通构建不会创建私钥或更改系统信任。

## 验证与贡献

```powershell
dotnet run --project tests/ArchiveInspector.Tests -c Release
dotnet run --project tests/BridgeStore.Tests -c Release
dotnet run --project tests/InstanceCoordinator.Tests -c Release
```

这些测试生成合成数据和独立进程作用域，不需要微信、ChatGPT 或真实聊天。Windows 分享诊断器见 [ShareProbe](tests/ShareProbe/README.md)，它不在 CI 中执行真实桌面分享。

欢迎提交可复现的缺陷报告和范围清晰的改动。请先阅读 [CONTRIBUTING.md](CONTRIBUTING.md)，安全问题通过[私密报告](https://github.com/bwjoke/ChatPCBridge/security/advisories/new)提交。

## 范围

已实现原始归档接收、备用 TXT、历史记录、单窗口协调及剪贴板交接。尚未实现自动粘贴、自动发送、Obsidian 归档、多 Agent 路由或云端同步。应用的分享入口需要 MSIX 注册，单独运行 EXE 不会注册到微信菜单。

[MIT License](LICENSE) · 设计来源与第三方说明见 [NOTICE.md](NOTICE.md)。
