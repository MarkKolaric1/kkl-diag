# Builds addons\kwp2000\kw1281test.exe: upstream KW1281Test plus kwp2000.patch (KWP2000 fault codes for ABS etc.).
# Needs git and the .NET 10 SDK.
$ErrorActionPreference = 'Stop'
$dir = Join-Path $PSScriptRoot 'kw1281test'
$out = Join-Path (Split-Path $PSScriptRoot -Parent) 'addons\kwp2000'

if (-not (Test-Path $dir)) {
    git clone https://github.com/gmenounos/kw1281test $dir; if ($LASTEXITCODE) { throw 'git clone failed' }
    git -C $dir checkout -q d68519566b4786ac35896c087c4abfa7abc4c261; if ($LASTEXITCODE) { throw 'git checkout failed' }
    git -C $dir apply (Join-Path $PSScriptRoot 'kwp2000.patch'); if ($LASTEXITCODE) { throw 'patch failed' }
}

dotnet publish (Join-Path $dir 'kw1281test.csproj') -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $out
if ($LASTEXITCODE) { throw 'dotnet publish failed' }
"Built: $out\kw1281test.exe"
