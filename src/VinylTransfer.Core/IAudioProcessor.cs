namespace VinylTransfer.Core;

public interface IAudioProcessor
{
    ProcessingResult Process(ProcessingRequest request);
}
