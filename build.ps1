<#
  Сборка MTRTracer в один exe.

  .\build.ps1                       самодостаточный exe (~80 МБ, .NET на целевом ПК не нужен)
  .\build.ps1 -FrameworkDependent   маленький exe (нужен установленный .NET 10 Desktop Runtime)
  .\build.ps1 -SkipTests            без юнит-тестов
  .\build.ps1 -Linux                ещё и Linux-версия (linux-x64) кросс-сборкой из Windows
  .\build.ps1 -Linux -LinuxArch arm64

  Результат: dist\MTRTracer.exe
#>
param(
    [switch]$FrameworkDependent,
    [switch]$SkipTests,
    [switch]$Linux,
    [ValidateSet('x64', 'arm64')][string]$LinuxArch = 'x64'
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Need($cmd, $hint) {
    if (-not (Get-Command $cmd -ErrorAction SilentlyContinue)) {
        Write-Host "Не найдено: $cmd. $hint" -ForegroundColor Red
        exit 1
    }
}

Need 'dotnet' 'Установите .NET 10 SDK: winget install Microsoft.DotNet.SDK.10'
Need 'npm' 'Установите Node.js 20+: winget install OpenJS.NodeJS.LTS'

Step '1/3 Интерфейс (React + Vite) -> ui\dist'
Push-Location ui
try {
    if (Test-Path package-lock.json) { npm ci } else { npm install }
    if ($LASTEXITCODE -ne 0) { throw 'npm install завершился с ошибкой' }
    npm run build
    if ($LASTEXITCODE -ne 0) { throw 'сборка интерфейса завершилась с ошибкой' }
}
finally { Pop-Location }

if (-not $SkipTests) {
    Step '2/3 Юнит-тесты (офлайн, в сеть не ходят)'
    dotnet test src\MTRTracer.Tests -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'тесты не прошли' }
}
else {
    Step '2/3 Тесты пропущены'
}

Step '3/3 Публикация exe'
if (Test-Path dist) { Remove-Item dist -Recurse -Force }
$publish = @('publish', 'src\MTRTracer.App', '-c', 'Release', '-r', 'win-x64', '-o', 'dist', '--nologo', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true')
if ($FrameworkDependent) {
    $publish += @('--self-contained', 'false')
}
else {
    $publish += @('--self-contained', 'true', '-p:EnableCompressionInSingleFile=true')
}
dotnet @publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish завершился с ошибкой' }

$exe = Join-Path $PSScriptRoot 'dist\MTRTracer.exe'
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "`nГотово: $exe ($size МБ)" -ForegroundColor Green

if ($Linux) {
    Step "Linux: публикация linux-$LinuxArch (кросс-сборка)"
    $rid = "linux-$LinuxArch"
    $outL = Join-Path $PSScriptRoot "dist-linux\$rid"
    if (Test-Path $outL) { Remove-Item $outL -Recurse -Force }
    dotnet publish src\MTRTracer.Linux -c Release -r $rid --self-contained true -o $outL --nologo `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish (Linux) завершился с ошибкой' }
    Copy-Item src\MTRTracer.Linux\mtrtracer.png, packaging\linux\mtrtracer.desktop, packaging\linux\install.sh $outL
    $tarFile = Join-Path $PSScriptRoot "dist-linux\mtrtracer-$rid.tar.gz"
    if (Test-Path $tarFile) { Remove-Item $tarFile -Force }
    tar -czf $tarFile -C (Join-Path $PSScriptRoot 'dist-linux') $rid
    Write-Host "Готово: $tarFile" -ForegroundColor Green
    Write-Host 'На Linux: tar -xzf mtrtracer-linux-*.tar.gz; cd linux-*; chmod +x MTRTracer install.sh; ./install.sh'
}
