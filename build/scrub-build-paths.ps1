# Removes build-machine paths from the files that ship with the mod.
#
# Two sources leak them: the Burst native stubs, which embed the absolute path of
# the toolchain's Burst cache, and their .pdb side files, which are never loaded at
# runtime. Everything else (managed assembly, managed .pdb, UI bundle) is kept clean
# by the compiler settings in CS2MultiplayerMod.csproj.
#
# Byte replacement is length-preserving, so the surrounding binary layout is untouched.

param([Parameter(Mandatory = $true)][string[]]$Directory)

$ErrorActionPreference = 'Stop'

# Any absolute Windows path that runs through a user profile. Separators may be
# doubled (the Burst stubs keep an escaped linker command line), and the match is
# bounded to path-legal characters so it cannot run away into neighbouring bytes.
$pathPattern = '(?i)[A-Za-z]:\\{1,2}Users\\{1,2}[^\x00-\x1f"<>|*?]{0,240}'

function Get-Filler([int]$length) {
    $tag = '[redacted]'
    if ($length -le $tag.Length) { return '_' * $length }
    return $tag + ('_' * ($length - $tag.Length))
}

$latin1 = [System.Text.Encoding]::GetEncoding(28591)

foreach ($dir in $Directory) {
    if (-not (Test-Path -LiteralPath $dir)) { continue }

    # Native symbol files: pure build-machine metadata, useless to players.
    Get-ChildItem -LiteralPath $dir -Filter '*_x86_64.pdb' -File -ErrorAction SilentlyContinue |
        ForEach-Object {
            Remove-Item -LiteralPath $_.FullName -Force
            Write-Output "scrub: removed $($_.Name)"
        }

    foreach ($file in Get-ChildItem -LiteralPath $dir -File -Include '*.dll', '*.so', '*.bundle' -Recurse) {
        $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
        $text = $latin1.GetString($bytes)
        $matched = [regex]::Matches($text, $pathPattern)
        if ($matched.Count -eq 0) { continue }

        foreach ($m in $matched) {
            $filler = $latin1.GetBytes((Get-Filler $m.Length))
            [System.Array]::Copy($filler, 0, $bytes, $m.Index, $filler.Length)
        }
        [System.IO.File]::WriteAllBytes($file.FullName, $bytes)
        Write-Output "scrub: redacted $($matched.Count) path(s) in $($file.Name)"
    }

    # Anything still carrying a profile path would ship as-is, so say so loudly.
    foreach ($file in Get-ChildItem -LiteralPath $dir -File -Recurse) {
        $text = $latin1.GetString([System.IO.File]::ReadAllBytes($file.FullName))
        $left = [regex]::Matches($text, $pathPattern)
        if ($left.Count -gt 0) {
            Write-Output "scrub: WARNING $($file.Name) still contains $($left.Count) build path(s), e.g. $($left[0].Value)"
        }
    }
}
