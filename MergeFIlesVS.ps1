# Folder aktualnie otwarty w PowerShell.
$root = (Get-Location).Path

# Nowy plik wynikowy z unikalną nazwą.
$outputName = "Kod-projektu-" + [guid]::NewGuid().ToString("N") + ".txt"
$outputPath = Join-Path $root $outputName

# Typy plików przydatne do dokumentacji projektu.
$extensions = @(
    ".cs", ".xaml", ".csproj", ".sln", ".slnx",
    ".props", ".targets", ".resx",
    ".vb", ".vbproj", ".fs", ".fsproj",
    ".razor", ".cshtml", ".html", ".css",
    ".js", ".jsx", ".ts", ".tsx", ".md"
)

# Foldery pomijane razem z całą zawartością.
$excludedFolders = @(
    ".vs", ".git", "bin", "obj",
    "packages", "node_modules", "TestResults"
)

function Get-ProjectFiles {
    param([string]$Folder)

    foreach ($item in Get-ChildItem -LiteralPath $Folder -Force -ErrorAction Stop) {
        # Nie podążaj za dowiązaniami do innych folderów.
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            continue
        }

        if ($item.PSIsContainer) {
            if ($excludedFolders -notcontains $item.Name) {
                Get-ProjectFiles -Folder $item.FullName
            }
        }
        elseif ($extensions -contains $item.Extension) {
            $item
        }
    }
}

$files = @(Get-ProjectFiles -Folder $root | Sort-Object FullName)

if ($files.Count -eq 0) {
    throw "Nie znaleziono plików kodu. Sprawdź, czy otwarto folder projektu."
}

$utf8 = New-Object System.Text.UTF8Encoding($true)
$writer = New-Object System.IO.StreamWriter($outputPath, $false, $utf8)

try {
    $writer.WriteLine("KOD PROJEKTU")
    $writer.WriteLine("Liczba plików: " + $files.Count)
    $writer.WriteLine()

    foreach ($file in $files) {
        $relativePath = $file.FullName.Substring($root.Length).TrimStart([char[]]"\/")

        $writer.WriteLine()
        $writer.WriteLine("============================================================")
        $writer.WriteLine("PLIK: " + $relativePath)
        $writer.WriteLine("============================================================")
        $writer.WriteLine()

        $content = [System.IO.File]::ReadAllText($file.FullName)
        $writer.WriteLine($content)
    }
}
finally {
    $writer.Dispose()
}

Write-Host ""
Write-Host "Gotowe. Połączono plików: $($files.Count)"
Write-Host "Plik wynikowy: $outputPath"