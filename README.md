# vinyl-transfer-declick

Application to process digitized vinyls to remove pops, clicks, and other noise.

## Project layout

- `src/VinylTransfer.Core`: Core audio models and processing contracts.
- `src/VinylTransfer.Infrastructure`: WAV I/O and infrastructure services.
- `src/VinylTransfer.UI`: Avalonia UI shell with placeholders for waveform/spectrogram views.

## Next steps

- Implement DSP pipeline in `VinylTransfer.Core` with auto/manual modes.
- Wire up UI actions to load WAV files, run processing, and export results.
- Replace waveform/spectrogram placeholders with rendered views.
