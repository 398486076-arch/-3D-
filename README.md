# T0 透明 3D 立方体桌面插件

一个悬浮在 Windows 桌面的 **6cm³ 透明 3D 立方体**，六面可嵌入照片，支持拖拽旋转、平移、缩放，数据本地持久化。

## 功能特性
- 透明悬浮背景，不挡桌面内容
- 左键拖拽旋转 + 惯性滑动；双击复位
- Shift+左键平移；滚轮缩放窗口本身
- 右键换图（六面各一张）；左键单击面看大图
- 系统托盘控制、全局热键（Ctrl+Shift+H 显隐 / Ctrl+Shift+R 复位）
- 照片数据存储在 `%LocalAppData%\T0Prototype\`，每台机器独立

## 系统要求
- Windows 10 / 11（64 位）
- 仅自己编译运行需 .NET 8 SDK；使用预打包版无需安装任何环境

## 获取方式
### 方式 A：下载预打包（推荐）
到本仓库 **Releases** 页面下载：
- **分发-自包含绿色版.zip**（约 64MB）：解压后双击 `T0Prototype.exe` 即用，无需安装运行环境
- **分发-轻量运行时版.zip**（约 89KB）：体积小，但需本机已安装 .NET 8 桌面运行时

### 方式 B：从源码编译
1. 安装 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
2. 双击 `run.bat` 自动还原、构建并启动

## 操作说明
| 操作 | 效果 |
|------|------|
| 左键拖拽 | 旋转立方体（松手有惯性） |
| 双击 | 复位视角 |
| Shift + 左键拖拽 | 平移立方体 |
| 滚轮 | 缩放窗口大小 |
| 右键 | 给当前面换照片 |
| 左键单击面 | 放大查看该面照片 |

## 技术栈
WPF (.NET 8) + 原生 `System.Windows.Media.Media3D`，无 WebView2 / 第三方 3D 引擎。

## 目录说明
- `MainWindow.xaml` / `MainWindow.xaml.cs`：主窗口与 3D 场景、交互逻辑
- `T0Prototype.csproj` / `run.bat`：工程定义与一键运行脚本
- `Assets/`：应用图标资源
