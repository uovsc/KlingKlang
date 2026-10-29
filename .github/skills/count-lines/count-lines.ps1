cls

$root = (Get-Location).Path
$extensions = @('.cs', '.xml', '.js', '.axaml')
$excludePathPattern = '\\(bin|obj|\.git|installer)\\'

$files = Get-ChildItem -Path $root -Recurse -File |
    Where-Object {
        ($extensions -contains $_.Extension) -and
        ($_.FullName -notmatch $excludePathPattern)
    } |
    Sort-Object FullName

$report = foreach ($file in $files) {
    $relativePath = $file.FullName.Substring($root.Length).TrimStart('\')
    $lineCount = (Get-Content -Path $file.FullName | Measure-Object -Line).Lines

    [PSCustomObject]@{
        Path = $relativePath.Replace('\', '/')
        Lines = $lineCount
    }
}

$report | ForEach-Object {
    "{0}`t{1}" -f $_.Lines, $_.Path
}

"SUM`t{0}" -f (($report | Measure-Object -Property Lines -Sum).Sum)
