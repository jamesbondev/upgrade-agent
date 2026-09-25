# Helpers shared by the scripts in this folder.

<#
.SYNOPSIS
    Runs a native command and throws if it fails.
.DESCRIPTION
    By default the command talks to the console directly (its output streams, and it can prompt).
    With -PassThru its output is returned instead, and shown before the error if it fails.
#>
function Invoke-Checked {
    param(
        [Parameter(Mandatory)] [string] $File,
        [string[]] $Arguments = @(),
        [switch] $PassThru
    )
    if ($PassThru) {
        $output = & $File @Arguments
        if ($LASTEXITCODE -ne 0) {
            $output | Out-Host
            throw "'$File $($Arguments -join ' ')' failed with exit code $LASTEXITCODE."
        }
        return $output
    }

    & $File @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "'$File $($Arguments -join ' ')' failed with exit code $LASTEXITCODE." }
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
