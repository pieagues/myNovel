# myNovel 一键发布脚本：commit → push → 建 Release（附 vsix）
# 用法：在自己的 PowerShell 里运行（不要用 Trae 内置终端）
#   powershell -ExecutionPolicy Bypass -File publish.ps1
#
# 如果 gh CLI 未登录，脚本会提示一次授权（浏览器 device code）。
# git push 用 Windows GCM 里已有的 GitHub 凭据，不需要额外登录。

$ErrorActionPreference = 'Stop'
Set-Location 'E:\WORK_SPACE\TRAE_WORKSPACE\myNovel'

$version = '1.0.9'
$tag = "v$version"
$vsix = 'dist\MyNovel.vsix'
$repo = 'pieagues/myNovel'

# ---- 1. 确认改动干净 ----
Write-Host "[1/3] 暂存并提交改动..." -ForegroundColor Cyan
git status --short

git add CHANGELOG.md src/ReaderControl.cs src/source.extension.vsixmanifest src/source.extension.vsixmanifest obj/ dist/ -ErrorAction SilentlyContinue
# 只暂存源代码变更，跳过构建产物
git reset HEAD -- obj/ dist/ -ErrorAction SilentlyContinue

git add CHANGELOG.md src/ReaderControl.cs src/source.extension.vsixmanifest

$msg = "fix: 章首上一页严重阻塞并越界跳到文档尾部 (v$version)"
git commit -m $msg
if ($LASTEXITCODE -ne 0) {
    Write-Host "Commit 可能已存在，继续..." -ForegroundColor Yellow
}

# ---- 2. Push ----
Write-Host "[2/3] 推送到 origin/main..." -ForegroundColor Cyan
git push origin main
if ($LASTEXITCODE -ne 0) { throw "push 失败" }

Write-Host ""
Write-Host "最近提交：" -ForegroundColor DarkGray
git log --oneline -3

# ---- 3. gh Release ----
Write-Host ""
Write-Host "[3/3] 创建 GitHub Release $tag..." -ForegroundColor Cyan

# 确保 gh 在 PATH
$env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ';' + [System.Environment]::GetEnvironmentVariable("Path","User")

try {
    $ghStatus = gh auth status 2>&1
    Write-Host "gh 已登录" -ForegroundColor Green
} catch {
    Write-Host "gh 未登录，请在弹出的浏览器中授权..." -ForegroundColor Yellow
    gh auth login --hostname github.com --git-protocol https --web
    if ($LASTEXITCODE -ne 0) { throw "gh 登录失败" }
}

# 确认 vsix 存在
if (-not (Test-Path $vsix)) { throw "找不到 $vsix，先跑 build.ps1" }

# 检查这个 tag 是否已经存在
$existing = gh release view $tag --repo $repo 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Host "Release $tag 已存在，跳过创建。如果要更新，先手动删旧 Release。" -ForegroundColor Yellow
    exit 0
}

# 从 CHANGELOG 提取版本说明
$notes = "## $tag

### 修复

- **章首按上一页长时间阻塞并跳到文档尾部**：`JumpToChapterEnd`（用于回退上一章末尾）之前调 `EnsurePageCacheAt(MaxCachedPages-1)`（MaxCachedPages=20000），意味着从目标章节开始会把整本小说剩余所有章节的页都算进缓存。长文上这个过程要阻塞 UI 数秒，期间翻页毫无反应；算完后直接跳到文档尾部附近的章节。现在改为只算出「覆盖本章末尾的那一页」就停，不越界扫描。"

gh release create $tag `
    --repo $repo `
    --title $tag `
    --notes $notes `
    $vsix

if ($LASTEXITCODE -ne 0) { throw "gh release create 失败" }

Write-Host ""
Write-Host "=== 发布完成 ===" -ForegroundColor Green
Write-Host "代码： https://github.com/$repo"
Write-Host "Release: https://github.com/$repo/releases/tag/$tag"
Write-Host "vsix:   $vsix"
