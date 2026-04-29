$file = "E:\VisStudioProjects\TopStepTrader\src\TopStepTrader.UI\Views\SuperTrendPlusView.xaml"
$bytes = [System.IO.File]::ReadAllBytes($file)
$text = [System.Text.Encoding]::UTF8.GetString($bytes)

# Helper: build a string from raw UTF-8 byte sequences (the garbled literals stored in file)
function BytesToStr([byte[]]$b) { [System.Text.Encoding]::UTF8.GetString($b) }

# 📈 U+1F4C8  — bytes C3 B0 C5 B8 E2 80 9C CB 86
$chart  = BytesToStr([byte[]](0xC3,0xB0,0xC5,0xB8,0xE2,0x80,0x9C,0xCB,0x86))
# 💡 U+1F4A1  — bytes C3 B0 C5 B8 E2 80 99 C2 A1
$bulb   = BytesToStr([byte[]](0xC3,0xB0,0xC5,0xB8,0xE2,0x80,0x99,0xC2,0xA1))
# ST×  — bytes C3 83 E2 80 94  (Ã×  double-encoded × then em-dash)
$stx    = BytesToStr([byte[]](0xC3,0x83,0xE2,0x80,0x94))
# ⏸ U+23F8 Pause  — bytes C3 A2 C2 8F C2 B8
$pause  = BytesToStr([byte[]](0xC3,0xA2,0xC2,0x8F,0xC2,0xB8))
# ▲ U+25B2 (hide arrow) — bytes C3 A2 E2 80 93 C2 B2
$arrowU = BytesToStr([byte[]](0xC3,0xA2,0xE2,0x80,0x93,0xC2,0xB2))
# ▼ U+25BC (show arrow) — bytes C3 A2 E2 80 93 C2 BC
$arrowD = BytesToStr([byte[]](0xC3,0xA2,0xE2,0x80,0x93,0xC2,0xBC))

$text = $text.Replace($chart,  "&#x1F4C8;")
$text = $text.Replace($bulb,   "&#x1F4A1;")
$text = $text.Replace($stx,    "ST&#xD7;")
$text = $text.Replace($pause,  "&#x23F8;")
$text = $text.Replace($arrowU, "&#x25B2;")
$text = $text.Replace($arrowD, "&#x25BC;")

# Write back as UTF-8 without BOM (strip leading BOM byte 0x3F artefact if present)
$outBytes = [System.Text.Encoding]::UTF8.GetBytes($text)
# If file started with BOM 0xEF 0xBB 0xBF but was mangled to 0x3F, fix first byte
if ($outBytes[0] -eq 0x3F) { $outBytes = $outBytes[1..($outBytes.Length-1)] }
[System.IO.File]::WriteAllBytes($file, $outBytes)
Write-Host "XAML encoding fixed. Replacements applied."
