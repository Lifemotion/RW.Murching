<#
.SYNOPSIS
    Downloads the public-domain / CC test media used while developing Bwl.Murching into ./samples.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$samples = Join-Path $root 'samples'
New-Item -ItemType Directory -Force $samples | Out-Null

$files = @(
    # Blender Foundation "Sintel" trailer, CC-BY 3.0. English dialogue over music: hallucination stress test.
    @{ Name = 'sintel_trailer-480p.mp4'; Url = 'https://download.blender.org/durian/trailer/sintel_trailer-480p.mp4' },
    # John F. Kennedy, Rice University, 1962-09-12. US government work, public domain. 24 min of English speech.
    @{ Name = 'jfk_rice_1962.mp4'; Url = 'https://archive.org/download/QdL2vJgQvTEVmTtxp4oAxOxEhHxCUJ/tmpiyw5_qhj.mp4' },
    # LibriVox recordings (public domain), Russian.
    @{ Name = 'pushkin_krasavitse_ru.mp3'; Url = 'https://archive.org/download/krasavitse-librivox/alexander-pushkin-krasavitse_64kb.mp3' },
    @{ Name = 'ezop_basni_01_ru.mp3'; Url = 'https://archive.org/download/aesops_fables_russian_0905_librivox/basni_01_ezop_64kb.mp3' }
)

foreach ($f in $files) {
    $target = Join-Path $samples $f.Name
    if (Test-Path $target) {
        Write-Host "exists  $($f.Name)"
        continue
    }
    Write-Host "fetch   $($f.Name)"
    Invoke-WebRequest -Uri $f.Url -OutFile $target -UseBasicParsing
}
Write-Host "done -> $samples"
