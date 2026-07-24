# DistanceLab

用于材料原位拉伸图像序列的跨平台双点距离追踪器。界面使用 Avalonia 12，追踪后端使用 C++20 和 OpenCV 4；同一套产品代码支持 Windows x64、Apple Silicon macOS 和 Intel macOS。

## 功能

- 可缩放的比例尺标定，以及 P1/P2 亚像素位置调整
- CLAHE 追踪输入与多种独立显示增强方法
- Lucas–Kanade 光流、前后向误差检查、局部模板恢复
- 固定首帧参考、可信关键帧、ZNCC、PSR 和亚像素相关峰拟合
- 丢点安全暂停与人工补点
- 两帧并行预取、瞬时读取失败重试、损坏帧跳过和诊断日志
- 实时曲线、CSV 重载、参数持久化和起始点截图
- Windows 自包含发布目录和签名/公证友好的 macOS `.app`

## 项目结构

```text
src/OpenCVDistanceLab.Core       平台无关模型、设置、CSV 和图像序列逻辑
src/OpenCVDistanceLab.Interop    UTF-8 C ABI 与原生句柄封装
src/OpenCVDistanceLab.App        Avalonia 桌面产品界面
native/                          CMake/OpenCV 跨平台追踪库
packaging/windows                Windows 构建与运行库收集
packaging/macos                  .app、图标、dylib、签名和公证
tests/                           无第三方测试框架的 Core 自检
```

## 前置条件

- Windows 一键构建：Visual Studio 2022“使用 C++ 的桌面开发”工作负载
- macOS/手动构建：.NET 10 SDK（LTS）、CMake 3.24+ 和 OpenCV 4.x C++ 开发包
- macOS：Xcode Command Line Tools

Windows 脚本会自动使用 Visual Studio 自带的 CMake，并将缺少的 .NET 10 SDK 和经过
SHA-256 校验的 OpenCV 4.13 下载到 `%LOCALAPPDATA%\DistanceLab`。这些依赖不会安装到
系统目录，后续构建会直接复用缓存。

## macOS 构建

Apple Silicon：

```bash
export OpenCV_DIR=/path/to/opencv/lib/cmake/opencv4
./build.sh osx-arm64
```

Intel：

```bash
./build.sh osx-x64
```

产物位于：

```text
artifacts/products/osx-arm64/DistanceLab.app
```

脚本会递归收集非系统 `.dylib` 并重写加载路径。本机测试使用临时签名；正式发布时设置：

```bash
export APPLE_SIGN_IDENTITY="Developer ID Application: ..."
export APPLE_NOTARY_PROFILE="notarytool-keychain-profile"
./build.sh osx-arm64
```

## Windows 构建

```powershell
.\build.ps1
```

首次构建需要联网下载约 500 MB 的依赖；后续可离线重复构建。脚本会依次完成原生库编译、
Core/原生/发布版自检、自包含 .NET 发布以及 ZIP 打包。

高级用法：

```powershell
.\build.ps1 -Configuration Debug
.\build.ps1 -OpenCvDir "D:\opencv\build"
.\build.ps1 -SkipArchive
```

解压版产物位于 `artifacts\publish\win-x64`；可分发 ZIP 位于
`artifacts\DistanceLab-<版本>-win-x64.zip`。二者均包含自包含 .NET 运行时、原生追踪库、
OpenCV 和 MSVC 应用本地运行库。

## 自检

Core：

```bash
dotnet run --project tests/OpenCVDistanceLab.Core.Tests
```

原生库会在平台构建过程中自动运行自检。已发布产品也支持：

```bash
DistanceLab --native-self-test
```

## 输出

选择的图像目录中会生成：

- `tracking_results.csv`
- `tracking_start_snapshots/`
- `tracking_error.log`（仅发生错误或跳帧时）

CSV 包含两个点的像素坐标、像素/实际距离、相对初始长度变化、工程应变、匹配质量和匹配路径。
