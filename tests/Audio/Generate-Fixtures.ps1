param(
    [string]$FFmpegPath = (Get-Command ffmpeg -CommandType Application).Source,
    [switch]$IncludeExistingFormats
)
$ErrorActionPreference = 'Stop'
function Convert-Fixture([string]$Name, [string[]]$Inputs) {
    & $FFmpegPath -hide_banner -loglevel error -y @Inputs (Join-Path $PSScriptRoot $Name)
    if ($LASTEXITCODE -ne 0) { throw "Synthetic fixture generation failed: $Name" }
}
$tone = 'sine=frequency=330:sample_rate=44100:duration=5'
if ($IncludeExistingFormats) {
    Convert-Fixture 'stereo-noise.mp3' @('-f','lavfi','-i','anoisesrc=color=white:seed=173:sample_rate=44100:duration=5','-ac','2','-c:a','libmp3lame','-b:a','128k')
    Convert-Fixture 'sine-stereo.ogg' @('-f','lavfi','-i',$tone,'-ac','2','-c:a','libvorbis','-q:a','3')
}
Convert-Fixture 'sine-stereo.aac' @('-f','lavfi','-i',$tone,'-ac','2','-c:a','aac','-b:a','128k','-f','adts')
Convert-Fixture 'sine-stereo.m4a' @('-f','lavfi','-i',$tone,'-ac','2','-c:a','aac','-b:a','128k')
Convert-Fixture 'sine-alac.m4a' @('-f','lavfi','-i',$tone,'-ac','2','-c:a','alac')
Convert-Fixture 'video-audio.mp4' @('-f','lavfi','-i','color=c=black:s=16x16:r=1:d=5','-f','lavfi','-i',$tone,'-map','0:v','-map','1:a','-c:v','libx264','-pix_fmt','yuv420p','-ac','2','-c:a','aac','-b:a','128k')
Convert-Fixture 'video-two-audio.mp4' @('-f','lavfi','-i','color=c=black:s=16x16:r=1:d=5','-f','lavfi','-i',$tone,'-f','lavfi','-i','sine=frequency=880:sample_rate=44100:duration=5','-map','0:v','-map','1:a','-map','2:a','-c:v','libx264','-pix_fmt','yuv420p','-ac','2','-c:a','aac','-b:a','128k')
Convert-Fixture 'video-only.mp4' @('-f','lavfi','-i','color=c=black:s=16x16:r=1:d=5','-c:v','libx264','-pix_fmt','yuv420p')
Convert-Fixture 'video-long-tail.mp4' @('-f','lavfi','-i','color=c=black:s=16x16:r=1:d=12','-f','lavfi','-i',$tone,'-map','0:v','-map','1:a','-c:v','libx264','-pix_fmt','yuv420p','-ac','2','-c:a','aac','-b:a','128k')
