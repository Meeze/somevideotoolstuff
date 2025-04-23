using System.Collections.Generic;

namespace VideoOverlayApi.models;

public class MediaItem
{
    public string FileName { get; set; }
    public bool IsImage { get; set; }
    public double From { get; set; }
    public double Until { get; set; }
    public string Base64Content { get; set; }
    public string AudioBase64 { get; set; }
    public bool Mute { get; set; }
    public double ClipVolume { get; set; } = 1.0; // NEW: volume for original clip audio
    public double AttachedVolume { get; set; } = 1.0; // NEW: volume for attached audio
}

public class TextOverlay
{
    public string Text { get; set; }
    public double From { get; set; }
    public double Until { get; set; }
    public int FontSize { get; set; }
    public string Color { get; set; }
    public double Fade { get; set; }
    public string Position { get; set; }
}

public class OverlayRequest
{
    public List<MediaItem> MediaItems { get; set; }
    public List<TextOverlay> Overlays { get; set; }
    public List<BackgroundAudioItem> BackgroundAudioItems { get; set; }
    public string Resolution { get; set; } // "720p", "1080p", or "automatic"
}

public class BackgroundAudioItem
{
    public string FileName { get; set; }
    public double Start { get; set; }
    public double From { get; set; }
    public double Duration { get; set; }
    public string Base64Content { get; set; }
    public double Volume { get; set; } = 1.0; // NEW: volume for this clip
}