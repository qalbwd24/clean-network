# 开发入口

项目使用 .NET Framework 4.8 的 WPF 和系统内置程序集，不需要 NuGet 包。

| 模块 | 用途 |
|---|---|
| `Program.cs` | 程序入口、单实例和测试入口 |
| `MainWindow.cs`、`assets/Main.xaml` | 界面和任务交互 |
| `Models.cs` | 设置、结果与本地数据 |
| `ProxyService.cs` | 读取节点、独立代理和网络测试 |
| `BrowserService.cs`、`BrowserSessions.cs` | CDP、时区、节点标识和窗口隔离 |
| `QualityReport.cs` | 后台报告、网站摘录和 HTML 输出 |
| `AutoStart.cs` | 当前用户启动项和连接就绪检查 |

`src/build.mjs` 生成编译器响应文件，`构建.cmd` 调用系统编译器。构建输出、测试数据和运行日志均被忽略。更新主程序前应正常退出正在运行的实例，保留用户数据目录。

测试文件和真实报告在 `outputs/checks/`、`outputs/reports/`，不公开提交。评分解析测试应使用虚构页面数据；真实网站挑战页必须标记为需人工验证，不可视为评分结果。
