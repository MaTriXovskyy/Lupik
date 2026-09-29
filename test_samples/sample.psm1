function Get-LupikFormat {
    param([string]$Path)
    [System.IO.Path]::GetExtension($Path)
}
Export-ModuleMember -Function Get-LupikFormat
