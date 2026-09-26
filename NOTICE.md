# Attribution and third-party components

ChatPCBridge is an independent C# / WPF implementation for Windows. Its share-target workflow was inspired by [freestylefly/WeChatBridge](https://github.com/freestylefly/WeChatBridge), an MIT-licensed macOS project. WeChatBridge credits [Dukou](https://github.com/qzz0518/Dukou) as its upstream. Their work helped establish the pattern of accepting user-exported archives, preserving the originals and handing them to another application.

This repository does not vendor their Swift sources or represent an official Windows release of either project. Any future copied or adapted source must retain the applicable copyright and license notices.

The application uses Microsoft .NET, WPF and Windows platform APIs. Self-contained build outputs include runtime components under their respective licenses; retain the notices supplied with those components. Microsoft Windows SDK build tools are downloaded for local packaging and are not source files of this project.

The package icons are generated from original geometric drawing code in `scripts/New-Assets.ps1`. No WeChat, ChatGPT or upstream project logo is included. Product names identify compatibility only and remain the property of their respective owners.
