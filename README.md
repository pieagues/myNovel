# mynovel

在 Visual Studio 里读 TXT 小说的扩展。界面是一个停靠工具窗口，配色取自 VS 主题、字体和字号取自你的编辑器设置，所以看起来和 IDE 本身没有区别。

## 功能

- **跟随主题**：背景/前景使用 VS 的「工具窗口」主题色，切换主题自动跟随
- **跟随字体**：字体族与字号读取「文本编辑器」的设置，大小与代码一致
- **分页阅读**：按窗口尺寸自动分页，窗口缩放后重新排版且不丢位置
- **章节目录**：自动识别「第X章 / 序章 / 楔子 / 番外 / Chapter N」等标题
- **阅读记忆**：记录每本书的位置，下次打开自动回到原处
- **滚轮翻页**：光标在阅读窗口时，上滚上一页、下滚下一页
- **一键隐藏**：`Esc` 或 `H` 收起窗口，`Ctrl+Alt+H` 全局切换显示

## 快捷键

| 快捷键 | 作用 |
| --- | --- |
| `Ctrl+Alt+N` | 打开小说文件（全局） |
| `Ctrl+Alt+H` | 显示 / 隐藏阅读窗口（全局） |
| `→` `↓` `Space` `PgDn` | 下一页 |
| `←` `↑` `PgUp` | 上一页 |
| `Home` / `End` | 跳到文首 / 末页 |
| `G` | 打开 / 关闭章节目录（`↑↓` 选择，`Enter` 跳转） |
| `O` | 在当前窗口打开另一本 |
| `Esc` / `H` | 隐藏窗口 |
| 鼠标滚轮 | 翻页（目录打开时滚动目录） |

菜单入口：`工具` 菜单、`视图 → 其他窗口 → mynovel 阅读器`、`.txt` 文件右键「用 mynovel 阅读」。

## 构建

需要本机已安装 Visual Studio 2022（提供 Roslyn 编译器与 Shell SDK 程序集）。VSCT 编译器首次构建时会自动从 NuGet 拉取 `Microsoft.VSSDK.BuildTools`。

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

产物：`dist/MyNovel.vsix`

## 安装

支持的 IDE：Visual Studio 2022（17.0 及以上，Community / Professional / Enterprise）与 Visual Studio 2026（18.x），x64 与 ARM64 均可。

1. **先完全退出 Visual Studio**（`文件 → 退出`，并确认任务管理器里没有 `devenv.exe`）——安装器在 IDE 运行时无法写入扩展目录。
2. 双击 `MyNovel.vsix`，或执行：

```powershell
& "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\Common7\IDE\VSIXInstaller.exe" "dist\MyNovel.vsix"
```

   装在非默认路径时，先找到本机的安装器所在目录再执行。
3. 安装器可能弹出**「此扩展可能不稳定或与当前 Visual Studio 版本不兼容」**的警告。这是因为扩展未做代码签名、且清单声明的版本区间较宽，属于保守提示，**勾选继续安装即可**，不影响使用。
4. 若 Windows SmartScreen 提示「未知发布者」，选择「更多信息 → 仍要运行」。
5. 重启 Visual Studio，按 `Ctrl+Alt+N` 打开小说。

卸载：`扩展 → 管理扩展 → 已安装`，找到 mynovel 点「卸载」。

> 注：Visual Studio 2019（16.x）不受支持。扩展依赖 17.0 才引入的 Shell 程序集版本，在 16.x 上会因绑定失败而无法加载。

## 数据位置

阅读进度保存在 `%APPDATA%\MyNovel\library.json`，删除该文件即可清空所有进度。

## 项目结构

```
src/
  MyNovelPackage.cs        扩展入口：注册命令与工具窗口
  MyNovelPackage.vsct      命令表：菜单项、右键菜单、快捷键
  ReaderToolWindow.cs      工具窗口宿主
  ReaderControl.cs         阅读界面：主题/字体、分页、目录、进度
  NovelDocument.cs         编码识别与章节解析
  ReadState.cs             阅读进度持久化
build.ps1                  构建脚本（vsct → 程序集 → pkgdef → vsix）
```

## 支持的编码

UTF-8（含 BOM）、UTF-16/32（含 BOM）、GBK / GB18030，其余按 UTF-8 严格校验后自动兜底。

## 更新记录

每次发版都在 [CHANGELOG.md](CHANGELOG.md) 里追加一条，并同步 `src/source.extension.vsixmanifest` 中的 `Version`。

