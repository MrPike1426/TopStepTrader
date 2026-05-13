$lines = Get-Content "Docs/LLD.md" -Encoding UTF8; $result = $lines[0..27] + $lines[36..($lines.Count-1)]; $result | Set-Content "Docs/LLD.md" -Encoding UTF8
