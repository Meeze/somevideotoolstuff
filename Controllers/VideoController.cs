using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using VideoOverlayApi.models;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;

namespace VideoOverlayApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class VideoController : ControllerBase
{
    private readonly string _outputDir = "C:/tooldata/output";

    [RequestSizeLimit(int.MaxValue)]
    [HttpPost("overlay")]
    public async Task<IActionResult> OverlayTextJson([FromBody] OverlayRequest request)
    {
        if (request.MediaItems == null || request.MediaItems.Count == 0)
            return BadRequest("At least one media item is required.");

        await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official);

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var tempFiles = new List<string>();
        var trimmedVideos = new List<string>();

        for (var i = 0; i < request.MediaItems.Count; i++)
        {
            var trimmedPath =
                await ProcessMediaItemAsync(request.MediaItems[i], timestamp, i, tempFiles, request.Resolution);
            trimmedVideos.Add(trimmedPath);
        }

        var concatenatedPath = await ConcatenateMediaAsync(trimmedVideos, timestamp, tempFiles);

        var finalOutputPath = concatenatedPath;
        if (request.Overlays != null && request.Overlays.Any())
            finalOutputPath = await ApplyTextOverlaysAsync(concatenatedPath, request.Overlays, timestamp, tempFiles);

        var totalDuration = request.MediaItems.Sum(item => item.Until - item.From);

        if (request.BackgroundAudioItems != null && request.BackgroundAudioItems.Any())
        {
            var bgAudioPath =
                await ProcessBackgroundAudioAsync(request.BackgroundAudioItems, totalDuration, timestamp, tempFiles);
            finalOutputPath =
                await MergeBackgroundAudioAsync(finalOutputPath, bgAudioPath, totalDuration, timestamp, tempFiles);
        }
        else
        {
            var silentBgAudioPath = Path.Combine(_outputDir, $"silent_bg_audio_{timestamp}.mp3");

            var silenceConversion = FFmpeg.Conversions.New()
                .AddParameter($"-f lavfi -t {totalDuration} -i anullsrc=channel_layout=stereo:sample_rate=44100")
                .AddParameter("-c:a libmp3lame -q:a 4")
                .SetOutput(silentBgAudioPath);

            await silenceConversion.Start();
            tempFiles.Add(silentBgAudioPath);

            finalOutputPath = await MergeBackgroundAudioAsync(finalOutputPath, silentBgAudioPath, totalDuration,
                timestamp, tempFiles);
        }

        CleanupTempFiles(tempFiles, finalOutputPath);
        return File(System.IO.File.OpenRead(finalOutputPath), "video/mp4", $"final_{timestamp}.mp4");
    }

    private async Task<string> ProcessMediaItemAsync(MediaItem item, long timestamp, int index, List<string> tempFiles,
        string resolution)
    {
        var duration = item.Until - item.From;
        var inputPath = Path.Combine(_outputDir, $"input_{timestamp}_{index}_{item.FileName}");
        var trimmedPath = Path.Combine(_outputDir, $"trimmed_{timestamp}_{index}.mp4");

        var fileBytes = Convert.FromBase64String(item.Base64Content);
        await System.IO.File.WriteAllBytesAsync(inputPath, fileBytes);
        tempFiles.Add(inputPath);

        var hasAttachedAudio = !string.IsNullOrWhiteSpace(item.AudioBase64);
        string audioPath = null;

        if (hasAttachedAudio)
        {
            audioPath = Path.Combine(_outputDir, $"audio_{timestamp}_{index}.mp3");
            var audioBytes = Convert.FromBase64String(item.AudioBase64);
            await System.IO.File.WriteAllBytesAsync(audioPath, audioBytes);
            tempFiles.Add(audioPath);
        }

        var clipVol = item.ClipVolume;
        var attachedVol = item.AttachedVolume;

        var scaleParam = resolution switch
        {
            "720p" => "-vf scale=1280:720",
            "1080p" => "-vf scale=1920:1080",
            "1440p" => "-vf scale=2560:1440",
            "4k" => "-vf scale=3840:2160",
            _ => null // automatic: no scaling
        };

        if (item.IsImage)
        {
            var imageToVideo = FFmpeg.Conversions.New()
                .AddParameter($"-loop 1 -i \"{inputPath}\"", ParameterPosition.PreInput);

            if (hasAttachedAudio)
            {
                imageToVideo.AddParameter($"-i \"{audioPath}\"", ParameterPosition.PreInput);

                if (attachedVol != 1.0)
                {
                    var attachedVolStr = attachedVol.ToString("0.0", CultureInfo.InvariantCulture);
                    imageToVideo.AddParameter($"-filter:a \"volume={attachedVolStr}\"");
                }

                imageToVideo.AddParameter("-shortest");
                imageToVideo.AddParameter($"-t {duration}");
                imageToVideo.AddParameter("-map 0:v:0 -map 1:a:0");
            }
            else
            {
                // Inject silence exactly matching duration
                imageToVideo.AddParameter(
                    $"-f lavfi -t {duration} -i anullsrc=channel_layout=stereo:sample_rate=44100");
                imageToVideo.AddParameter("-map 0:v:0 -map 1:a:0");
                imageToVideo.AddParameter($"-t {duration}");
                imageToVideo.AddParameter("-shortest");
            }

            if (!string.IsNullOrEmpty(scaleParam)) imageToVideo.AddParameter(scaleParam);

            imageToVideo.AddParameter("-r 30"); // Force framerate for image clips
            imageToVideo.AddParameter("-c:v libx264 -preset veryfast -pix_fmt yuv420p");
            imageToVideo.AddParameter($"-t {duration}");
            imageToVideo.SetOutput(trimmedPath);

            await imageToVideo.Start();
        }
        else
        {
            var trimConversion = FFmpeg.Conversions.New()
                .AddParameter($"-ss {item.From}", ParameterPosition.PreInput)
                .AddParameter($"-to {item.Until}", ParameterPosition.PreInput)
                .AddParameter($"-i \"{inputPath}\"", ParameterPosition.PreInput);

            if (item.Mute && !hasAttachedAudio)
            {
                // Inject silence with exact duration for muted clip
                trimConversion.AddParameter(
                    $"-f lavfi -t {duration} -i anullsrc=channel_layout=stereo:sample_rate=44100");
                trimConversion.AddParameter("-map 0:v:0 -map 1:a:0");
                trimConversion.AddParameter($"-t {duration}");
            }
            else if (item.Mute && hasAttachedAudio)
            {
                trimConversion.AddParameter($"-i \"{audioPath}\"");

                if (attachedVol != 1.0)
                {
                    var attachedVolStr = attachedVol.ToString("0.0", CultureInfo.InvariantCulture);
                    trimConversion.AddParameter($"-filter:a \"volume={attachedVolStr}\"");
                }

                trimConversion.AddParameter("-map 0:v:0 -map 1:a:0");
                trimConversion.AddParameter($"-t {duration}");
            }
            else if (!item.Mute && hasAttachedAudio)
            {
                trimConversion.AddParameter($"-i \"{audioPath}\"");

                var clipVolStr = clipVol.ToString("0.0", CultureInfo.InvariantCulture);
                var attachedVolStr = attachedVol.ToString("0.0", CultureInfo.InvariantCulture);

                string audioFilter;

                if (attachedVol != 1.0)
                    audioFilter =
                        $"[0:a]volume={clipVolStr}[ca];[1:a]volume={attachedVolStr}[aa];[ca][aa]amix=inputs=2:duration=shortest[aout]";
                else
                    audioFilter =
                        $"[0:a]volume={clipVolStr}[ca];[ca][1:a]amix=inputs=2:duration=shortest[aout]";

                trimConversion.AddParameter($"-filter_complex \"{audioFilter}\"");
                trimConversion.AddParameter("-map 0:v:0 -map \"[aout]\"");
                trimConversion.AddParameter($"-t {duration}");
            }
            else if (!item.Mute && !hasAttachedAudio)
            {
                if (clipVol != 1.0)
                    trimConversion.AddParameter(
                        $"-filter:a \"volume={clipVol.ToString("0.0", CultureInfo.InvariantCulture)}\"");

                trimConversion.AddParameter($"-t {duration}");
            }

            if (!string.IsNullOrEmpty(scaleParam)) trimConversion.AddParameter(scaleParam);

            trimConversion.SetOutput(trimmedPath);
            await trimConversion.Start();
        }

        tempFiles.Add(trimmedPath);
        return trimmedPath;
    }


    private async Task<string> ConcatenateMediaAsync(List<string> videoPaths, long timestamp, List<string> tempFiles)
    {
        var concatListPath = Path.Combine(_outputDir, $"concat_list_{timestamp}.txt");
        var concatListContent =
            string.Join(Environment.NewLine, videoPaths.Select(v => $"file '{v.Replace("\\", "/")}'"));
        await System.IO.File.WriteAllTextAsync(concatListPath, concatListContent);
        tempFiles.Add(concatListPath);

        var concatenatedPath = Path.Combine(_outputDir, $"concatenated_{timestamp}.mp4");
        var concatConversion = FFmpeg.Conversions.New()
            .AddParameter($"-f concat -safe 0 -i \"{concatListPath}\"", ParameterPosition.PreInput)
            .AddParameter("-c copy")
            .SetOutput(concatenatedPath);

        await concatConversion.Start();
        tempFiles.Add(concatenatedPath);
        return concatenatedPath;
    }

    private async Task<string> ApplyTextOverlaysAsync(string inputPath, List<TextOverlay> overlays, long timestamp,
        List<string> tempFiles)
    {
        var filterChain = new List<string>();

        foreach (var overlay in overlays)
        {
            var x = "(w-text_w)/2";
            var y = "(h-text_h)/2";

            if (!string.IsNullOrWhiteSpace(overlay.Position))
            {
                var parts = overlay.Position.Split(' ');
                if (parts.Length >= 2)
                {
                    x = parts[0];
                    y = parts[1];
                }
            }

            var color = string.IsNullOrWhiteSpace(overlay.Color) ? "white" : overlay.Color;

            var alphaExpr = overlay.Fade > 0
                ? $"if(lt(t\\,{overlay.From + overlay.Fade}), (t-{overlay.From})/{overlay.Fade}, if(lt(t\\,{overlay.Until - overlay.Fade}), 1, if(lt(t\\,{overlay.Until}), ({overlay.Until}-t)/{overlay.Fade}, 0)))"
                : "1";

            var drawText =
                $"drawtext=text='{overlay.Text}':fontfile='/Windows/Fonts/arial.ttf':fontcolor={color}:fontsize={overlay.FontSize}:" +
                $"x={x}:y={y}:enable='between(t\\,{overlay.From}\\,{overlay.Until})':alpha='{alphaExpr}'";

            filterChain.Add(drawText);
        }

        var fullFilter = string.Join(",", filterChain);
        var finalOutputPath = Path.Combine(_outputDir, $"final_{timestamp}.mp4");

        var overlayConversion = FFmpeg.Conversions.New()
            .AddParameter($"-i \"{inputPath}\"", ParameterPosition.PreInput)
            .AddParameter($"-vf \"{fullFilter}\"")
            .AddParameter("-codec:a copy")
            .SetOutput(finalOutputPath);

        await overlayConversion.Start();
        tempFiles.Add(finalOutputPath);
        return finalOutputPath;
    }

    private async Task<string> ProcessBackgroundAudioAsync(List<BackgroundAudioItem> bgAudioItems, double totalDuration,
        long timestamp, List<string> tempFiles)
    {
        var bgAudioFiles = new List<string>();

        for (var i = 0; i < bgAudioItems.Count; i++)
        {
            var item = bgAudioItems[i];
            var bgInputPath = Path.Combine(_outputDir, $"input_bg_audio_{timestamp}_{i}_{item.FileName}");
            var trimmedPath = Path.Combine(_outputDir, $"trimmed_bg_audio_{timestamp}_{i}.mp3");

            var bgBytes = Convert.FromBase64String(item.Base64Content);
            await System.IO.File.WriteAllBytesAsync(bgInputPath, bgBytes);
            tempFiles.Add(bgInputPath);

            var trimConversion = FFmpeg.Conversions.New()
                .AddParameter($"-i \"{bgInputPath}\"", ParameterPosition.PreInput)
                .AddParameter($"-ss {item.From}")
                .AddParameter($"-t {item.Duration}")
                .AddParameter("-c copy")
                .SetOutput(trimmedPath);

            await trimConversion.Start();
            tempFiles.Add(trimmedPath);

            var delayedPath = Path.Combine(_outputDir, $"delayed_bg_audio_{timestamp}_{i}.mp3");
            var delayMs = (int)(item.Start * 1000);
            var volumeStr = item.Volume.ToString("0.0", CultureInfo.InvariantCulture);

            var delayConversion = FFmpeg.Conversions.New()
                .AddParameter($"-i \"{trimmedPath}\"", ParameterPosition.PreInput)
                .AddParameter($"-af \"adelay={delayMs}|{delayMs},volume={volumeStr}\"")
                .AddParameter("-c:a libmp3lame -q:a 4")
                .SetOutput(delayedPath);

            await delayConversion.Start();
            tempFiles.Add(delayedPath);
            bgAudioFiles.Add(delayedPath);
        }

        var mixedBgAudioPath = Path.Combine(_outputDir, $"mixed_bg_audio_{timestamp}.mp3");

        if (bgAudioFiles.Count == 1)
            return bgAudioFiles[0];

        var mixInputs = string.Join(" ", bgAudioFiles.Select(f => $"-i \"{f}\""));
        var amixInputs = bgAudioFiles.Count;

        var mixCommand = FFmpeg.Conversions.New()
            .AddParameter(mixInputs, ParameterPosition.PreInput)
            .AddParameter($"-filter_complex \"amix=inputs={amixInputs}:duration=longest[aout]\"")
            .AddParameter("-map \"[aout]\"")
            .AddParameter("-c:a libmp3lame -ar 44100 -ac 2 -b:a 192k")
            .SetOutput(mixedBgAudioPath);

        await mixCommand.Start();
        tempFiles.Add(mixedBgAudioPath);

        return mixedBgAudioPath;
    }

    private async Task<string> MergeBackgroundAudioAsync(string videoPath, string bgAudioPath, double totalDuration,
        long timestamp, List<string> tempFiles)
    {
        var paddedBgAudioPath = Path.Combine(_outputDir, $"padded_bg_audio_{timestamp}.mp3");

        var padBgAudio = FFmpeg.Conversions.New()
            .AddParameter($"-i \"{bgAudioPath}\"", ParameterPosition.PreInput)
            .AddParameter($"-f lavfi -t {totalDuration} -i anullsrc=channel_layout=stereo:sample_rate=44100")
            .AddParameter("-filter_complex \"[0:a][1:a]amix=inputs=2:duration=longest[aout]\"")
            .AddParameter("-map \"[aout]\"")
            .AddParameter("-c:a libmp3lame -q:a 4")
            .SetOutput(paddedBgAudioPath);

        await padBgAudio.Start();
        tempFiles.Add(paddedBgAudioPath);

        var finalWithBgAudioPath = Path.Combine(_outputDir, $"final_with_bg_audio_{timestamp}.mp4");

        var mergeBgAudioConversion = FFmpeg.Conversions.New()
            .AddParameter($"-i \"{videoPath}\"", ParameterPosition.PreInput)
            .AddParameter($"-i \"{paddedBgAudioPath}\"", ParameterPosition.PreInput)
            .AddParameter("-filter_complex \"[0:a][1:a]amix=inputs=2:duration=first[aout]\"")
            .AddParameter("-map 0:v:0 -map \"[aout]\"")
            .AddParameter($"-t {totalDuration}")
            .AddParameter("-c:v copy -c:a aac")
            .SetOutput(finalWithBgAudioPath);

        await mergeBgAudioConversion.Start();
        tempFiles.Add(finalWithBgAudioPath);

        if (System.IO.File.Exists(videoPath))
            System.IO.File.Delete(videoPath);

        return finalWithBgAudioPath;
    }

    private void CleanupTempFiles(List<string> tempFiles, string finalOutputPath)
    {
        foreach (var file in tempFiles.Where(f => f != finalOutputPath))
            if (System.IO.File.Exists(file))
                System.IO.File.Delete(file);
    }
}