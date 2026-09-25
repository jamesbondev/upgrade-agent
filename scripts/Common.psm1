# Helpers shared by the scripts in this folder.

<#
.SYNOPSIS
    Runs a native command and throws if it fails. Returns its output.
#>
function Invoke-Checked {
    param(
        [Parameter(Mandatory)] [string] $File,
        [string[]] $Arguments = @()
    )
    $output = & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "'$File $($Arguments -join ' ')' failed with exit code $LASTEXITCODE." }
    $output
}

<#
.SYNOPSIS
    True when $Path is $Root or inside it. Compares whole path segments, so ".ua-work2" is not inside ".ua-work".
#>
function Test-PathInside {
    param([Parameter(Mandatory)] [string] $Path, [Parameter(Mandatory)] [string] $Root)
    $root = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Root))
    $full = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Path))
    $full.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or
        $full.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

Export-ModuleMember -Function Invoke-Checked, Test-PathInside
