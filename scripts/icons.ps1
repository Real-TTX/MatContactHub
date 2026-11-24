#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Draws the PNG icons of MatContactHub from the SVG logo (scripts/logo.js writes them) with Edge headless: the PWA icons (192, 512, maskable 512),
    the apple-touch-icon (180), the favicons (32, 16), docs/logo/matcontacthub-mark-512.png and the sheet of all variants docs/logo/preview.png
    (from docs/logo/preview.html, which scripts/logo.js writes too).
.EXAMPLE
    node scripts/logo.js; ./scripts/icons.ps1
#>
$root = Split-Path -Parent $PSScriptRoot
$logo = Join-Path $root 'src/MatContactHub/wwwroot/img/logo'
$img = Join-Path $root 'src/MatContactHub/wwwroot/img'
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge was not found.' }

$work = Join-Path ([IO.Path]::GetTempPath()) "matcontacthub-icons-$([guid]::NewGuid().ToString('n'))"
New-Item -ItemType Directory -Force $work | Out-Null

function Shoot([string]$page, [int]$width, [int]$height, [string]$target) {
    # Edge draws the page into a png of the size of its window; the old file goes first, so the wait below waits for the new one
    Remove-Item -Force $target -ErrorAction SilentlyContinue
    & $edge --headless=new --disable-gpu --hide-scrollbars --default-background-color=00000000 "--window-size=$width,$height" "--screenshot=$target" "file:///$($page -replace '\\','/')" 2>$null | Out-Null
    $deadline = (Get-Date).AddSeconds(20)
    while (-not (Test-Path $target) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
    if (-not (Test-Path $target)) { throw "Edge did not draw $target" }
    Write-Host "drew $target"
}

function Draw([string]$svg, [int]$size, [string]$target) {
    # an html page that shows the svg at exactly the size of the window, without a margin
    $html = Join-Path $work "icon-$size.html"
    $source = (Get-Content -Raw (Join-Path $logo $svg)) -replace '<svg ', "<svg width=`"$size`" height=`"$size`" "
    Set-Content -Encoding utf8 $html "<!doctype html><html><head><style>html,body{margin:0;padding:0;background:transparent;overflow:hidden}svg{display:block}</style></head><body>$source</body></html>"
    Shoot $html $size $size $target
}

try {
    Draw 'matcontacthub-mark.svg' 192 (Join-Path $img 'icon-192.png')
    Draw 'matcontacthub-mark.svg' 512 (Join-Path $img 'icon-512.png')
    Draw 'matcontacthub-maskable.svg' 512 (Join-Path $img 'icon-maskable-512.png')
    Draw 'matcontacthub-maskable.svg' 180 (Join-Path $img 'apple-touch-icon.png')
    Draw 'matcontacthub-favicon.svg' 32 (Join-Path $img 'favicon-32.png')
    Draw 'matcontacthub-favicon.svg' 16 (Join-Path $img 'favicon-16.png')
    Copy-Item (Join-Path $img 'icon-512.png') (Join-Path $root 'docs/logo/matcontacthub-mark-512.png') -Force
    Shoot (Join-Path $root 'docs/logo/preview.html') 1100 1660 (Join-Path $root 'docs/logo/preview.png')
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
