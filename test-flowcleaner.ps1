# FlowCleaner 快速测试脚本
# 用法: .\test-flowcleaner.ps1 <path-to-obfuscated-dll>

param(
    [Parameter(Mandatory=$false)]
    [string]$InputDll = "",
    
    [Parameter(Mandatory=$false)]
    [switch]$Verbose
)

$ErrorActionPreference = "Stop"

Write-Host "=== FlowCleaner 测试脚本 ===" -ForegroundColor Cyan
Write-Host ""

# 1. 检查是否已构建
$de4dotExe = Join-Path $PSScriptRoot "de4dot.cui\bin\Release\net8.0\de4dot.exe"
if (-not (Test-Path $de4dotExe)) {
    $de4dotExe = Join-Path $PSScriptRoot "de4dot.cui\bin\Release\net472\de4dot.exe"
}

if (-not (Test-Path $de4dotExe)) {
    Write-Host "❌ de4dot 未构建，正在构建..." -ForegroundColor Yellow
    Write-Host ""
    
    # 尝试构建
    if (Test-Path (Join-Path $PSScriptRoot "build.ps1")) {
        & (Join-Path $PSScriptRoot "build.ps1")
    } else {
        dotnet build (Join-Path $PSScriptRoot "de4dot.netcore.sln") -c Release
    }
    
    # 再次检查
    $de4dotExe = Join-Path $PSScriptRoot "de4dot.cui\bin\Release\net8.0\de4dot.exe"
    if (-not (Test-Path $de4dotExe)) {
        $de4dotExe = Join-Path $PSScriptRoot "de4dot.cui\bin\Release\net472\de4dot.exe"
    }
    
    if (-not (Test-Path $de4dotExe)) {
        Write-Host "❌ 构建失败或找不到 de4dot.exe" -ForegroundColor Red
        exit 1
    }
}

Write-Host "✅ 找到 de4dot: $de4dotExe" -ForegroundColor Green
Write-Host ""

# 2. 检查 FlowCleaner 是否注册
Write-Host "检查 FlowCleaner 注册状态..." -ForegroundColor Yellow
$helpOutput = & $de4dotExe --help 2>&1 | Out-String
if ($helpOutput -match "-p fc\s+FlowCleaner") {
    Write-Host "✅ FlowCleaner 已成功注册！" -ForegroundColor Green
} else {
    Write-Host "⚠️  FlowCleaner 可能未注册，但继续测试..." -ForegroundColor Yellow
}
Write-Host ""

# 3. 如果提供了输入文件，执行清理
if ($InputDll -and (Test-Path $InputDll)) {
    $inputPath = Resolve-Path $InputDll
    $outputPath = Join-Path (Split-Path $inputPath) "cleaned_$(Split-Path $inputPath -Leaf)"
    
    Write-Host "📦 输入: $inputPath" -ForegroundColor Cyan
    Write-Host "📦 输出: $outputPath" -ForegroundColor Cyan
    Write-Host ""
    
    # 构建参数
    $args = @()
    if ($Verbose) {
        $args += "-v"
    }
    $args += "-p", "fc", $inputPath, "-o", $outputPath
    
    Write-Host "🚀 执行反混淆..." -ForegroundColor Yellow
    Write-Host "命令: $de4dotExe $($args -join ' ')" -ForegroundColor Gray
    Write-Host ""
    
    & $de4dotExe @args
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host ""
        Write-Host "✅ 反混淆完成！" -ForegroundColor Green
        Write-Host ""
        Write-Host "下一步：" -ForegroundColor Cyan
        Write-Host "  1. 用 ILSpy 或 dnSpy 打开: $outputPath"
        Write-Host "  2. 查看 IdentityAppServiceBase 类（或其他目标类）"
        Write-Host "  3. 对比处理前后的差异"
        Write-Host ""
        
        # 显示文件大小对比
        $inputSize = (Get-Item $inputPath).Length / 1KB
        $outputSize = (Get-Item $outputPath).Length / 1KB
        Write-Host "文件大小对比:" -ForegroundColor Cyan
        Write-Host "  处理前: $([math]::Round($inputSize, 2)) KB"
        Write-Host "  处理后: $([math]::Round($outputSize, 2)) KB"
        
    } else {
        Write-Host ""
        Write-Host "❌ 反混淆失败 (退出码: $LASTEXITCODE)" -ForegroundColor Red
        exit $LASTEXITCODE
    }
    
} else {
    # 没有输入文件，显示使用说明
    Write-Host "📖 使用方法：" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "  .\test-flowcleaner.ps1 <path-to-dll> [-Verbose]" -ForegroundColor White
    Write-Host ""
    Write-Host "示例：" -ForegroundColor Cyan
    Write-Host "  .\test-flowcleaner.ps1 'C:\path\to\Volo.Abp.Identity.Pro.Application.dll'" -ForegroundColor Gray
    Write-Host "  .\test-flowcleaner.ps1 'C:\path\to\obfuscated.dll' -Verbose" -ForegroundColor Gray
    Write-Host ""
    Write-Host "或者直接使用 de4dot：" -ForegroundColor Cyan
    Write-Host "  $de4dotExe -p fc input.dll -o output.dll" -ForegroundColor Gray
    Write-Host ""
}

Write-Host "=== 完成 ===" -ForegroundColor Cyan
