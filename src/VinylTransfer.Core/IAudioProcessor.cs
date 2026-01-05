using System.Threading;

namespace VinylTransfer.Core;

public interface IAudioProcessor
{
    ProcessingResult Process(
        ProcessingRequest request,
        IProgress<ProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
