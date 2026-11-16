# 推送 1.0.6 改动到 GitHub
# 用法：在自己的 PowerShell 里运行（不要用 Trae 内置终端）
#   powershell -ExecutionPolicy Bypass -File commit-push.ps1

$ErrorActionPreference = 'Stop'
Set-Location 'E:\WORK_SPACE\TRAE_WORKSPACE\myNovel'

git add README.md CHANGELOG.md src/source.extension.vsixmanifest src/ReaderControl.cs
git commit -m 'feat: 适配 VS 2026 与 ARM64，正文排版收紧至 1.0.7'
git push

git log --oneline -2
git status --short
"---- 完成，可以前往 GitHub 建 Release ----"
