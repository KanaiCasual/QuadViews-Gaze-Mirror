# Applies patches\*.patch to the upstream submodules. Run once on a fresh clone (after `git submodule update --init
# --recursive`). Safe to run again: a submodule that already carries the changes is left alone.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$targets = @(
    @{ Repo = 'Quad-Views-Foveated'; Patch = 'quad-views-foveated.patch' },
    @{ Repo = 'OpenXR-Layer-OBSMirror'; Patch = 'openxr-layer-obsmirror.patch' }
)

foreach ($t in $targets) {
    $repo = Join-Path $root $t.Repo
    $patch = Join-Path $root "patches\$($t.Patch)"
    if (-not (Test-Path (Join-Path $repo '.git'))) { throw "$($t.Repo) is missing - run: git submodule update --init --recursive" }

    git -C $repo apply --check -R $patch 2>$null
    if ($LASTEXITCODE -eq 0) { "$($t.Repo): already patched."; continue }

    git -C $repo apply --check $patch
    if ($LASTEXITCODE -ne 0) { throw "$($t.Patch) does not apply to $($t.Repo). Is the submodule at the pinned commit?" }
    git -C $repo apply $patch
    "$($t.Repo): patched."
}
