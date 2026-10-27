# XML Minecraft Launcher · WinUI 3 客户端

一个 Windows 平台的 Minecraft 启动器，基于 **WinUI 3 + Windows App SDK**，
原生 AOT 编译，可打包成单文件 exe 直接运行，不需要安装。

> 界面使用 Fluent 风格，浅色主题，侧栏六个页面：概览 / 版本库 / 下载 / Mod 下载 / 联机 / 设置。

---

## 功能

- **版本管理**：读取 Mojang 官方版本清单，国内镜像（BMCLAPI）加速；支持安装原版、Fabric、Quilt、Forge、NeoForge
- **下载加速**：客户端 / 依赖库 / 资源文件走镜像，断点续传，多线程并发
- **Mod 下载**：搜索 Modrinth，按加载器与游戏版本筛选，直接装进选中实例
- **联机**：房主开好局域网世界后生成 6 位连接码，访客输入即可进同一个存档（经官方服务器中继）
- **账号**：微软正版登录（OAuth 设备码流程）、离线账号、站内 XML 账号（注册 / 登录 / 头像同步）
- **自动更新**：启动时检查新版本，软件内显示下载进度，下载完自动替换并重启
- **原生 AOT**：冷启动快、没有 coreclr.dll，配合单文件外壳可做成一个 exe

## 技术栈

| 用途 | 选择 |
| --- | --- |
| UI | WinUI 3（Windows App SDK）/ XAML |
| 运行时 | .NET 10，`PublishAot` + `SelfContained` |
| JSON | `System.Text.Json` 源生成（AOT 兼容） |
| HTTP | `HttpClient` + 镜像重写 |

## 目录结构

```
LinxLauncher.WinUI/
├── App.xaml / App.xaml.cs        应用入口、全局异常处理
├── MainWindow.xaml / .cs         主窗口：导航、账号面板、更新流程
├── Controls/                     自绘控件：下载步骤、启动进度、首次向导
├── Converters/                   XAML 值转换器
├── Models/                       Minecraft 版本 / 资源 / 清单模型
├── Services/                     业务层：版本、下载、账号、联机、更新、日志
├── Styles/                       颜色、字体、控件样式
├── Views/                        六个页面
└── Assets/                       图标与界面用的方块素材
```

## 构建

需要 **.NET 10 SDK** 与 **Windows 10 1809+ / Windows 11**。

```powershell
# 普通构建（调试用，依赖本机已安装的 Windows App Runtime）
dotnet build -c Debug

# 自包含发布（不依赖用户环境里的运行时）
dotnet publish -c Release -r win-x64 -p:SelfContained=true -o out

# 原生 AOT（体积小、启动快；产物仍是 exe + 一堆 dll）
dotnet publish -c Release -r win-x64 -p:PublishAot=true -p:SelfContained=true -o out
```

### 配置服务器地址

公告、更新检查、账号与联机都指向同一台自建服务器。真实地址不进仓库：

1. 把 `Services/LocalSecrets.example.cs.txt` 复制成 `Services/LocalSecrets.cs`
2. 改掉里面的 `ServerUrl`
3. 这个文件已在 `.gitignore` 中，不会被提交

不配置也能编译运行，只是公告、更新、账号、联机这些联网功能会连不上。

### 关于 Native AOT 的几个坑

项目已经踩过并处理过的点，改代码时注意：

- 列表控件的 `ItemsSource` 不能直接塞 `List<T>` / `ObservableCollection<T>`，
  用 `Services/UiList.cs` 的 `Fill / Add / Clear` 逐项塞 `Items`
- 被 `{Binding}` / `DisplayMemberPath` 用到的模型类要标
  `[Microsoft.UI.Xaml.Data.Bindable]`，且属性不能用 `init` / `required`
- `JsonSerializer.Serialize(JsonObject)` 在 AOT 下走反射会抛异常，
  统一用 `JsonNode.ToJsonString()`
- 需要反序列化的类型必须登记在 `Services/AppJsonContext.cs`，
  且不能是 `private` 嵌套类，否则源生成器不生成元数据
- `Assets` 目录要整目录挂 `CopyToPublishDirectory`，否则发布产物里没有图标，
  任务栏会显示系统默认图标

## 免责声明

- 本项目为个人学习与自用项目，与 **Mojang Studios / Microsoft** 无任何隶属或合作关系。
- 界面中的 Minecraft 方块图标、游戏名称等素材版权归 Mojang Studios 所有，仅作界面展示。
- 使用本软件造成的任何损失（存档损坏、账号异常等）由使用者自行承担。

## 许可

源码采用自定义协议，**个人学习与非商业自用免费；商业使用、二次分发、衍生发布需事先取得作者书面许可**。

完整条款见 [LICENSE](LICENSE)。
