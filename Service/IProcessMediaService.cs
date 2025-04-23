using System.Collections.Generic;
using System.Threading.Tasks;
using VideoOverlayApi.models;

namespace VideoOverlayApi.Service;

public interface IProcessMediaService
{
    Task<string> ProcessMediaItemAsync(MediaItem item, long timestamp, int index, List<string> tempFiles, string resolution);
}
