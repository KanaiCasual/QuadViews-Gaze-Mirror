# Regenerates patches\*.patch from the current (modified) state of the upstream submodules. Run after changing their
# sources, then commit the patch files. Only the files listed here are captured - upstream's build also rewrites a few
# generated files (version.h, resource.rc) that must not end up in a patch.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$targets = @(
    @{
        Repo = 'Quad-Views-Foveated'; Patch = 'quad-views-foveated.patch'
        Files = @('openxr-api-layer/layer.cpp', 'openxr-api-layer/eye_gaze_export.h')
    },
    @{
        Repo = 'OpenXR-Layer-OBSMirror'; Patch = 'openxr-layer-obsmirror.patch'
        Files = @('XR_APILAYER_NOVENDOR_OBSMirror/dx11mirror.cpp', 'XR_APILAYER_NOVENDOR_OBSMirror/dx11mirror.h',
                  'XR_APILAYER_NOVENDOR_OBSMirror/layer.cpp', 'XR_APILAYER_NOVENDOR_OBSMirror/eye_gaze_export.h')
    }
)

# eye_gaze_export.h is shared verbatim between the two layers: keep them identical.
$shared = $targets | ForEach-Object { Join-Path $root "$($_.Repo)\$($_.Files | Where-Object { $_ -like '*eye_gaze_export.h' })" }
if ((Get-FileHash $shared[0]).Hash -ne (Get-FileHash $shared[1]).Hash) {
    throw "eye_gaze_export.h differs between the two layers. Copy the newer one over the other first."
}

foreach ($t in $targets) {
    $repo = Join-Path $root $t.Repo
    $patch = Join-Path $root "patches\$($t.Patch)"
    # New files only show up in a diff once git knows about them ("intent to add" stages nothing).
    foreach ($file in $t.Files) { git -C $repo add -N -- $file 2>$null }
    git -C $repo diff --output=$patch -- @($t.Files)
    git -C $repo apply --check -R $patch
    if ($LASTEXITCODE -ne 0) { throw "The generated $($t.Patch) does not match the working tree." }
    '{0,-32} {1,8:N0} bytes' -f $t.Patch, (Get-Item $patch).Length
}
