<#
.SYNOPSIS
    Live Archive Encoder v7 项目文件一键整理脚本
.DESCRIPTION
    自动将错误放置在 src 根目录下的源码文件归位到 src/LiveArchiveEncoder/ 对应的子目录中。
#>

# 确保在 PowerShell 7 环境下运行
if ($PSVersionTable.PSVersion.Major -lt 7) {
    Write-Warning "建议使用 PowerShell 7 运行此脚本 (当前版本: $($PSVersionTable.PSVersion))"
}

$rootSrc = "src/LiveArchiveEncoder"
$folders = @("Encoding", "FFmpeg", "Frames")

Write-Host "=== 开始自动整理项目文件 ===" -ForegroundColor Cyan

foreach ($folder in $folders) {
    $sourceDir = Join-Path "src" $folder
    $targetDir = Join-Path $rootSrc $folder

    if (Test-Path $sourceDir) {
        # 确保目标子目录存在
        if (-not (Test-Path $targetDir)) {
            New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
            Write-Host "创建目标目录: $targetDir" -ForegroundColor DarkGray
        }

        Write-Host "正在处理文件夹: $folder" -ForegroundColor Yellow

        # 移动文件
        Get-ChildItem -Path $sourceDir -File -Recurse | ForEach-Object {
            $destFile = Join-Path $targetDir $_.Name
            Move-Item -Path $_.FullName -Destination $targetDir -Force
            Write-Host "  [移动] $($_.Name) -> $targetDir" -ForegroundColor Green
        }

        # 清理旧的空文件夹
        Remove-Item -Path $sourceDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "`n=== 文件整理完毕！建议执行以下命令重新编译 ===" -ForegroundColor Cyan
Write-Host "dotnet clean" -ForegroundColor White
Write-Host "dotnet build -c Release" -ForegroundColor White
Write-Host "dotnet run --project src/LiveArchiveEncoder/LiveArchiveEncoder.csproj" -ForegroundColor White